"""Photon CAD industrial container adapter.

This process accepts one bounded JSON request on stdin and emits one bounded JSON
response on stdout. It owns no transport, authentication, host paths, or user
supplied code. Library discovery is limited to the fixed imports below.
"""

from __future__ import annotations

import contextlib
import enum
from decimal import Decimal, InvalidOperation
import hashlib
import inspect
import io
import json
import math
import os
from pathlib import Path
import re
import signal
import stat
import struct
import sys
import types
import typing


REQUEST_SCHEMA = "photon.cad.industrial.request/v1"
RESPONSE_SCHEMA = "photon.cad.industrial.response/v1"
CATALOG_SCHEMA = "photon.cad.industrial.catalog/v1"
BASE_IMAGE_ID = "sha256:33d9c839840115640b08dd3c4142b7f29624329408155fe1484e1d88c3891703"
EXPECTED_BUILD123D_VERSION = "0.11.0"
EXPECTED_BD_WAREHOUSE_VERSION = "0.2.0"

MAX_REQUEST_BYTES = 1 * 1024 * 1024
MAX_RESPONSE_BYTES = 4 * 1024 * 1024
MAX_JSON_DEPTH = 64
MAX_JSON_NODES = 250_000
MAX_JSON_ABSOLUTE_NUMBER = Decimal("1000000000000")
MAX_MODEL_BYTES = 64 * 1024 * 1024
MAX_TOTAL_INPUT_BYTES = 256 * 1024 * 1024
MAX_GLB_BYTES = 128 * 1024 * 1024
MAX_SOURCES = 256
MAX_OCCURRENCES = 1_024
MAX_DAG_DEPTH = 256
MAX_CATALOG_ITEMS = 2_000
MAX_PARAMETERS = 32
MAX_CHOICES = 1_000
MAX_TRIANGLES = 2_000_000
MAX_ACCESSOR_ELEMENTS = 10_000_000
MAX_ABSOLUTE_COORDINATE = 1_000_000_000.0
OPERATION_TIMEOUT_SECONDS = 45.0
FIXED_EXPORT_TIMESTAMP = "1970-01-01T00:00:00"

INPUT_DIRECTORY = Path("/photon-input")
OUTPUT_DIRECTORY = Path("/photon-output")
PRIVATE_DIRECTORY = Path("/tmp/photon-industrial")

SAFE_IDENTIFIER = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$")
SAFE_SLOT = re.compile(r"^[a-z][a-z0-9_-]{0,63}$")
DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")


try:
    import importlib.metadata as package_metadata
    import build123d as build123d
    import bd_warehouse.bearing as warehouse_bearing
    import bd_warehouse.fastener as warehouse_fastener
    import bd_warehouse.flange as warehouse_flange
    import bd_warehouse.gear as warehouse_gear
    import bd_warehouse.open_builds as warehouse_open_builds
    import bd_warehouse.pipe as warehouse_pipe
    import bd_warehouse.sprocket as warehouse_sprocket
    import bd_warehouse.thread as warehouse_thread

    DEPENDENCIES_READY = True
except Exception:
    package_metadata = None
    build123d = None
    warehouse_bearing = None
    warehouse_fastener = None
    warehouse_flange = None
    warehouse_gear = None
    warehouse_open_builds = None
    warehouse_pipe = None
    warehouse_sprocket = None
    warehouse_thread = None
    DEPENDENCIES_READY = False


CATEGORY_SPECS = (
    ("bearings", "Bearings", warehouse_bearing),
    ("fasteners", "Fasteners", warehouse_fastener),
    ("flanges", "Flanges", warehouse_flange),
    ("gears", "Gears", warehouse_gear),
    ("o-rings", "O-rings", None),
    ("openbuilds", "OpenBuilds", warehouse_open_builds),
    ("pipes", "Pipes", warehouse_pipe),
    ("retaining-rings", "Retaining rings", None),
    ("shaft-keys", "Shaft keys", None),
    ("sprockets", "Sprockets", warehouse_sprocket),
    ("threads", "Threads", warehouse_thread),
)

CONTROL_PARAMETERS = frozenset(("rotation", "align", "mode", "path"))
ABSTRACT_OR_COMPOSITE_NAMES = frozenset(
    (
        "Bearing",
        "ClearanceHole",
        "Flange",
        "InsertHole",
        "InvoluteToothProfile",
        "Nut",
        "Pipe",
        "PipeSection",
        "PressFitHole",
        "Screw",
        "SpurGearPlan",
        "TapHole",
        "Thread",
        "ThreadedHole",
        "Washer",
    )
)

FIXED_ERRORS = frozenset(
    (
        "artifact-invalid",
        "catalog-mismatch",
        "dependency-unavailable",
        "internal-error",
        "invalid-json",
        "invalid-parameter",
        "invalid-request",
        "item-not-found",
        "operation-failed",
        "request-too-large",
        "resource-limit",
        "timeout",
        "unsupported-operation",
    )
)


class ProtocolFailure(Exception):
    def __init__(self, code: str):
        super().__init__(code)
        self.code = code if code in FIXED_ERRORS else "internal-error"


class OperationTimedOut(Exception):
    pass


class InvalidJson(Exception):
    pass


def _reject_duplicate_pairs(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise InvalidJson
        result[key] = value
    return result


def _reject_json_constant(_value: str) -> typing.NoReturn:
    raise InvalidJson


def _parse_json_integer(raw: str) -> int:
    if len(raw) > 32:
        raise InvalidJson
    try:
        value = int(raw, 10)
    except (TypeError, ValueError) as exception:
        raise InvalidJson from exception
    if abs(value) > MAX_JSON_ABSOLUTE_NUMBER:
        raise InvalidJson
    return value


def _parse_json_float(raw: str) -> float:
    if len(raw) > 128:
        raise InvalidJson
    try:
        decimal_value = Decimal(raw)
        value = float(decimal_value)
    except (InvalidOperation, OverflowError, ValueError) as exception:
        raise InvalidJson from exception
    if not decimal_value.is_finite() or abs(decimal_value) > MAX_JSON_ABSOLUTE_NUMBER:
        raise InvalidJson
    if not math.isfinite(value) or (decimal_value != 0 and value == 0):
        raise InvalidJson
    return value


def _strict_json(payload: bytes) -> object:
    if payload.startswith(b"\xef\xbb\xbf"):
        raise ProtocolFailure("invalid-json")
    try:
        text = payload.decode("utf-8", errors="strict")
        value = json.loads(
            text,
            object_pairs_hook=_reject_duplicate_pairs,
            parse_constant=_reject_json_constant,
            parse_int=_parse_json_integer,
            parse_float=_parse_json_float,
        )
    except (InvalidJson, UnicodeDecodeError, json.JSONDecodeError, RecursionError, ValueError) as exception:
        raise ProtocolFailure("invalid-json") from exception
    pending: list[tuple[object, int]] = [(value, 1)]
    inspected = 0
    while pending:
        current, depth = pending.pop()
        inspected += 1
        if inspected > MAX_JSON_NODES or depth > MAX_JSON_DEPTH:
            raise ProtocolFailure("invalid-json")
        if isinstance(current, str):
            if any(0xD800 <= ord(character) <= 0xDFFF for character in current):
                raise ProtocolFailure("invalid-json")
        elif type(current) is int:
            if abs(current) > MAX_JSON_ABSOLUTE_NUMBER:
                raise ProtocolFailure("invalid-json")
        elif type(current) is float:
            if not math.isfinite(current) or abs(Decimal(str(current))) > MAX_JSON_ABSOLUTE_NUMBER:
                raise ProtocolFailure("invalid-json")
        elif isinstance(current, list):
            pending.extend((item, depth + 1) for item in current)
        elif isinstance(current, dict):
            for key, item in current.items():
                if any(0xD800 <= ord(character) <= 0xDFFF for character in key):
                    raise ProtocolFailure("invalid-json")
                pending.append((item, depth + 1))
        elif current is not None and type(current) is not bool:
            raise ProtocolFailure("invalid-json")
    return value


def _canonical_json(value: object) -> bytes:
    try:
        return json.dumps(
            value,
            ensure_ascii=False,
            allow_nan=False,
            sort_keys=True,
            separators=(",", ":"),
        ).encode("utf-8")
    except (TypeError, ValueError) as exception:
        raise ProtocolFailure("internal-error") from exception


def _sha256(data: bytes) -> str:
    return "sha256:" + hashlib.sha256(data).hexdigest()


def _require_object(value: object) -> dict[str, object]:
    if not isinstance(value, dict) or any(not isinstance(key, str) for key in value):
        raise ProtocolFailure("invalid-request")
    return typing.cast(dict[str, object], value)


def _exact_keys(value: dict[str, object], required: set[str], optional: set[str] | None = None) -> None:
    optional = optional or set()
    keys = set(value)
    if not required.issubset(keys) or not keys.issubset(required | optional):
        raise ProtocolFailure("invalid-request")


def _safe_identifier(value: object) -> str:
    if not isinstance(value, str) or SAFE_IDENTIFIER.fullmatch(value) is None:
        raise ProtocolFailure("invalid-parameter")
    return value


def _safe_slot(value: object) -> str:
    if not isinstance(value, str) or SAFE_SLOT.fullmatch(value) is None:
        raise ProtocolFailure("invalid-parameter")
    return value


def _expected_digest(value: object) -> str:
    if not isinstance(value, str) or DIGEST.fullmatch(value) is None:
        raise ProtocolFailure("invalid-parameter")
    return value


def _finite_number(value: object, minimum: float, maximum: float) -> float:
    if type(value) not in (int, float):
        raise ProtocolFailure("invalid-parameter")
    result = float(value)
    if not math.isfinite(result) or result < minimum or result > maximum:
        raise ProtocolFailure("invalid-parameter")
    return result


def _humanize(value: str) -> str:
    words = re.sub(r"(?<=[a-z0-9])(?=[A-Z])", " ", value).replace("_", " ").strip()
    return words[:1].upper() + words[1:]


def _read_regular_file_no_follow(path: Path, maximum_bytes: int, require_single_link: bool) -> bytes:
    descriptor = -1
    try:
        flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
        descriptor = os.open(path, flags)
        before = os.fstat(descriptor)
        if (
            not stat.S_ISREG(before.st_mode)
            or before.st_size < 0
            or before.st_size > maximum_bytes
            or (require_single_link and before.st_nlink != 1)
        ):
            raise ProtocolFailure("artifact-invalid")
        chunks: list[bytes] = []
        remaining = before.st_size
        while remaining:
            chunk = os.read(descriptor, min(1_048_576, remaining))
            if not chunk:
                raise ProtocolFailure("artifact-invalid")
            chunks.append(chunk)
            remaining -= len(chunk)
        if os.read(descriptor, 1):
            raise ProtocolFailure("artifact-invalid")
        after = os.fstat(descriptor)
        if (
            before.st_dev != after.st_dev
            or before.st_ino != after.st_ino
            or before.st_size != after.st_size
            or before.st_mtime_ns != after.st_mtime_ns
        ):
            raise ProtocolFailure("artifact-invalid")
        return b"".join(chunks)
    except ProtocolFailure:
        raise
    except Exception as exception:
        raise ProtocolFailure("artifact-invalid") from exception
    finally:
        if descriptor >= 0:
            os.close(descriptor)


def _package_content_digest(distribution_name: str, package_prefix: str) -> tuple[str, str]:
    if package_metadata is None:
        raise ProtocolFailure("dependency-unavailable")
    try:
        distribution = package_metadata.distribution(distribution_name)
        version = distribution.version
        candidates: list[tuple[str, Path]] = []
        for entry in distribution.files or ():
            relative = str(entry).replace("\\", "/")
            first = relative.split("/", 1)[0]
            if relative.startswith(package_prefix + "/") or (
                first.startswith(package_prefix + "-") and first.endswith(".dist-info")
            ):
                candidates.append((relative, Path(distribution.locate_file(entry))))
        if not candidates:
            raise ProtocolFailure("dependency-unavailable")
        digest = hashlib.sha256()
        total = 0
        for relative, path in sorted(candidates, key=lambda item: item[0]):
            try:
                payload = _read_regular_file_no_follow(path, 32 * 1024 * 1024, require_single_link=False)
            except ProtocolFailure as exception:
                raise ProtocolFailure("dependency-unavailable") from exception
            total += len(payload)
            if total > 256 * 1024 * 1024:
                raise ProtocolFailure("resource-limit")
            relative_bytes = relative.encode("utf-8")
            digest.update(struct.pack("<I", len(relative_bytes)))
            digest.update(relative_bytes)
            digest.update(struct.pack("<Q", len(payload)))
            digest.update(payload)
        return version, "sha256:" + digest.hexdigest()
    except ProtocolFailure:
        raise
    except Exception as exception:
        raise ProtocolFailure("dependency-unavailable") from exception


def _runtime_identity() -> dict[str, object]:
    if not DEPENDENCIES_READY or package_metadata is None:
        raise ProtocolFailure("dependency-unavailable")
    try:
        build_version = package_metadata.version("build123d")
        warehouse_version, warehouse_digest = _package_content_digest("bd-warehouse", "bd_warehouse")
    except Exception as exception:
        if isinstance(exception, ProtocolFailure):
            raise
        raise ProtocolFailure("dependency-unavailable") from exception
    if build_version != EXPECTED_BUILD123D_VERSION or warehouse_version != EXPECTED_BD_WAREHOUSE_VERSION:
        raise ProtocolFailure("dependency-unavailable")
    package_id_source = _canonical_json(
        {
            "baseImageId": BASE_IMAGE_ID,
            "bdWarehouseDigest": warehouse_digest,
            "bdWarehouseVersion": warehouse_version,
            "build123dVersion": build_version,
        }
    )
    return {
        "baseImageId": BASE_IMAGE_ID,
        "build123dVersion": build_version,
        "library": {
            "name": "bd-warehouse",
            "version": warehouse_version,
            "contentDigest": warehouse_digest,
            "packageId": "bdw_" + hashlib.sha256(package_id_source).hexdigest(),
        },
    }


def _unwrap_optional(annotation: object) -> tuple[object, bool]:
    origin = typing.get_origin(annotation)
    if origin in (typing.Union, types.UnionType):
        arguments = list(typing.get_args(annotation))
        nullable = type(None) in arguments
        arguments = [argument for argument in arguments if argument is not type(None)]
        if nullable and len(arguments) == 1:
            return arguments[0], True
    return annotation, False


def _numeric_limits(name: str, kind: str, default: object) -> tuple[float | int, float | int]:
    lowered = name.lower()
    if kind == "integer":
        if "tooth" in lowered or lowered == "num_teeth":
            return 3, 1_000
        if "count" in lowered or "starts" in lowered or lowered.startswith("num_"):
            return (0 if default == 0 else 1), 1_000
        return -1_000_000, 1_000_000
    if "pressure_angle" in lowered:
        return 0.1, 89.0
    if "angle" in lowered:
        return -360.0, 360.0
    if any(token in lowered for token in ("clearance", "interference", "offset", "compensation")):
        return -1_000.0, 1_000.0
    return (0.0 if default == 0 else 0.000001), 1_000_000.0


def _annotation_parameter(
    name: str,
    annotation: object,
    default: object,
    choice_override: tuple[object, ...] | None,
) -> tuple[dict[str, object], typing.Callable[[object], object]] | None:
    annotation, nullable = _unwrap_optional(annotation)
    required = default is inspect.Parameter.empty
    descriptor: dict[str, object] = {
        "id": name,
        "label": _humanize(name),
        "required": required,
        "nullable": nullable,
    }
    converter: typing.Callable[[object], object]
    choices: tuple[object, ...] | None = choice_override
    origin = typing.get_origin(annotation)
    if choices is None and origin is typing.Literal:
        choices = tuple(typing.get_args(annotation))
    if choices is not None:
        if not choices or len(choices) > MAX_CHOICES or any(type(choice) not in (str, int, float, bool) for choice in choices):
            return None
        if len({json.dumps(choice, sort_keys=True) for choice in choices}) != len(choices):
            return None
        ordered = tuple(sorted(choices, key=lambda choice: (type(choice).__name__, str(choice))))
        descriptor["kind"] = "choice"
        descriptor["choices"] = list(ordered)

        def choose(value: object, allowed: tuple[object, ...] = ordered) -> object:
            if not any(type(value) is type(choice) and value == choice for choice in allowed):
                raise ProtocolFailure("invalid-parameter")
            return value

        converter = choose
    elif inspect.isclass(annotation) and issubclass(annotation, enum.Enum):
        members = tuple(member for member in annotation)
        values = tuple(member.value for member in members)
        if not values or len(values) > MAX_CHOICES or any(type(value) not in (str, int, float, bool) for value in values):
            return None
        descriptor["kind"] = "choice"
        descriptor["choices"] = list(values)

        def choose_enum(value: object, enum_members: tuple[enum.Enum, ...] = members) -> object:
            for member in enum_members:
                if type(value) is type(member.value) and value == member.value:
                    return member
            raise ProtocolFailure("invalid-parameter")

        converter = choose_enum
    elif annotation is bool:
        descriptor["kind"] = "boolean"

        def choose_bool(value: object) -> object:
            if type(value) is not bool:
                raise ProtocolFailure("invalid-parameter")
            return value

        converter = choose_bool
    elif annotation in (int, float):
        kind = "integer" if annotation is int else "number"
        minimum, maximum = _numeric_limits(name, kind, default)
        descriptor.update({"kind": kind, "minimum": minimum, "maximum": maximum})

        def choose_number(
            value: object,
            numeric_kind: str = kind,
            low: float | int = minimum,
            high: float | int = maximum,
        ) -> object:
            if numeric_kind == "integer":
                if type(value) is not int or value < low or value > high:
                    raise ProtocolFailure("invalid-parameter")
                return value
            return _finite_number(value, float(low), float(high))

        converter = choose_number
    else:
        return None
    if not required:
        if default is None:
            descriptor["default"] = None
        elif type(default) in (str, int, float, bool):
            descriptor["default"] = default
        else:
            return None
    original_converter = converter

    def nullable_converter(value: object) -> object:
        if value is None:
            if nullable:
                return None
            raise ProtocolFailure("invalid-parameter")
        return original_converter(value)

    return descriptor, nullable_converter


def _class_descriptors(
    category_id: str,
    module: object,
    class_name: str,
    candidate: type,
    package_id: str,
) -> list[tuple[dict[str, object], dict[str, object] | None]]:
    identity_prefix = {"category": category_id, "implementation": class_name, "packageId": package_id}
    title = _humanize(class_name)

    def item_id(variant: object) -> str:
        identity = dict(identity_prefix)
        identity["variant"] = variant
        return "bdw_" + hashlib.sha256(_canonical_json(identity)).hexdigest()[:48]

    def unsupported(identifier: str, item_title: str) -> tuple[dict[str, object], None]:
        return (
            {
                "id": identifier,
                "title": item_title,
                "category": category_id,
                "availability": "unsupported",
                "reason": "unsupported-constructor",
                "parameters": [],
            },
            None,
        )

    if class_name in ABSTRACT_OR_COMPOSITE_NAMES or inspect.isabstract(candidate):
        return [unsupported(item_id(None), title)]
    try:
        if build123d is None or not issubclass(candidate, build123d.Shape):
            raise TypeError
        signature = inspect.signature(candidate.__init__)
        hints = typing.get_type_hints(candidate.__init__, globalns=vars(module), localns={class_name: candidate})
    except Exception:
        return [unsupported(item_id(None), title)]
    parameters = [parameter for name, parameter in signature.parameters.items() if name != "self"]
    type_parameter = next((parameter for parameter in parameters if parameter.name.endswith("_type")), None)
    variants: list[tuple[object | None, dict[str, object]]] = [(None, {})]
    types_method = getattr(candidate, "types", None)
    if type_parameter is not None and callable(types_method):
        try:
            raw_variants = list(types_method())
            if not raw_variants or len(raw_variants) > 64 or any(type(value) not in (str, int) for value in raw_variants):
                raise ValueError
            variants = [(value, {type_parameter.name: value}) for value in sorted(raw_variants, key=str)]
        except Exception:
            return [unsupported(item_id(None), title)]
    results: list[tuple[dict[str, object], dict[str, object] | None]] = []
    for variant, fixed in variants:
        public_parameters: list[dict[str, object]] = []
        converters: dict[str, typing.Callable[[object], object]] = {}
        supported = True
        for parameter in parameters:
            if parameter.name in fixed:
                continue
            if parameter.name in CONTROL_PARAMETERS:
                if parameter.default is inspect.Parameter.empty:
                    supported = False
                continue
            choices: tuple[object, ...] | None = None
            if parameter.name == "size" and callable(getattr(candidate, "sizes", None)):
                try:
                    size_method = getattr(candidate, "sizes")
                    size_signature = inspect.signature(size_method)
                    raw_sizes = size_method(variant) if len(size_signature.parameters) == 1 else size_method()
                    values = tuple(raw_sizes)
                    if not values or len(values) > MAX_CHOICES:
                        supported = False
                        break
                    choices = values
                except Exception:
                    supported = False
                    break
            annotation = hints.get(parameter.name, parameter.annotation)
            described = _annotation_parameter(parameter.name, annotation, parameter.default, choices)
            if described is None:
                supported = False
                break
            public, converter = described
            public_parameters.append(public)
            converters[parameter.name] = converter
        if len(public_parameters) > MAX_PARAMETERS:
            supported = False
        variant_title = title if variant is None else f"{title} — {variant}"
        identifier = item_id(variant)
        if not supported:
            results.append(unsupported(identifier, variant_title))
            continue
        public_parameters.sort(key=lambda descriptor: typing.cast(str, descriptor["id"]))
        results.append(
            (
                {
                    "id": identifier,
                    "title": variant_title,
                    "category": category_id,
                    "availability": "supported",
                    "parameters": public_parameters,
                },
                {
                    "candidate": candidate,
                    "fixed": dict(fixed),
                    "parameters": {parameter["id"]: parameter for parameter in public_parameters},
                    "converters": converters,
                },
            )
        )
    return results


def _catalog() -> tuple[dict[str, object], dict[str, dict[str, object]], str]:
    identity = _runtime_identity()
    package_id = typing.cast(str, typing.cast(dict[str, object], identity["library"])["packageId"])
    categories: list[dict[str, object]] = []
    items: list[dict[str, object]] = []
    runtime_items: dict[str, dict[str, object]] = {}
    for category_id, title, module in CATEGORY_SPECS:
        if module is None:
            categories.append(
                {
                    "id": category_id,
                    "title": title,
                    "availability": "unsupported",
                    "reason": "not-installed-in-pinned-package",
                }
            )
            continue
        categories.append({"id": category_id, "title": title, "availability": "supported"})
        for class_name, candidate in inspect.getmembers_static(module, inspect.isclass):
            if class_name.startswith("_") or getattr(candidate, "__module__", None) != getattr(module, "__name__", None):
                continue
            for public_item, runtime_item in _class_descriptors(category_id, module, class_name, candidate, package_id):
                items.append(public_item)
                if runtime_item is not None:
                    runtime_items[typing.cast(str, public_item["id"])] = runtime_item
                if len(items) > MAX_CATALOG_ITEMS:
                    raise ProtocolFailure("resource-limit")
    categories.sort(key=lambda category: typing.cast(str, category["id"]))
    items.sort(
        key=lambda item: (
            typing.cast(str, item["category"]),
            typing.cast(str, item["title"]),
            typing.cast(str, item["id"]),
        )
    )
    document: dict[str, object] = {
        "schema": CATALOG_SCHEMA,
        "runtime": identity,
        "categories": categories,
        "items": items,
    }
    catalog_digest = _sha256(_canonical_json(document))
    return document, runtime_items, catalog_digest


def _validate_shape(shape: object) -> tuple[dict[str, object], object]:
    try:
        if build123d is None or not isinstance(shape, build123d.Shape):
            raise ValueError
        validity = getattr(shape, "is_valid")
        valid = validity() if callable(validity) else bool(validity)
        solids = shape.solids()
        bounds = shape.bounding_box()
        volume = float(shape.volume)
        minimum = [float(bounds.min.X), float(bounds.min.Y), float(bounds.min.Z)]
        maximum = [float(bounds.max.X), float(bounds.max.Y), float(bounds.max.Z)]
        coordinates = minimum + maximum
        if not valid or len(solids) < 1 or not math.isfinite(volume) or volume <= 0:
            raise ValueError
        if any(not math.isfinite(value) or abs(value) > MAX_ABSOLUTE_COORDINATE for value in coordinates):
            raise ValueError
        if any(minimum[index] > maximum[index] for index in range(3)):
            raise ValueError
        receipt = {
            "units": "millimeter",
            "volumeMm3": volume,
            "solidCount": len(solids),
            "bounds": {"minimum": minimum, "maximum": maximum},
        }
        return receipt, shape
    except Exception as exception:
        raise ProtocolFailure("artifact-invalid") from exception


def _step_bytes(shape: object) -> bytes:
    if build123d is None:
        raise ProtocolFailure("dependency-unavailable")
    try:
        stream = io.BytesIO()
        if not build123d.export_step(shape, stream, timestamp=FIXED_EXPORT_TIMESTAMP):
            raise ValueError
        payload = stream.getvalue()
        if not payload or len(payload) > MAX_MODEL_BYTES:
            raise ProtocolFailure("resource-limit")
        if not payload.startswith(b"ISO-10303-21;") or b"END-ISO-10303-21;" not in payload[-128:]:
            raise ValueError
        return payload
    except ProtocolFailure:
        raise
    except Exception as exception:
        raise ProtocolFailure("operation-failed") from exception


def _open_directory_no_follow(path: Path) -> int:
    descriptor = -1
    try:
        flags = (
            os.O_RDONLY
            | getattr(os, "O_CLOEXEC", 0)
            | getattr(os, "O_DIRECTORY", 0)
            | getattr(os, "O_NOFOLLOW", 0)
        )
        descriptor = os.open(path, flags)
        metadata = os.fstat(descriptor)
        if not stat.S_ISDIR(metadata.st_mode):
            raise ValueError
        return descriptor
    except Exception as exception:
        if descriptor >= 0:
            os.close(descriptor)
        raise ProtocolFailure("artifact-invalid") from exception


def _write_all(descriptor: int, payload: bytes) -> None:
    view = memoryview(payload)
    offset = 0
    while offset < len(view):
        written = os.write(descriptor, view[offset:])
        if written <= 0:
            raise OSError
        offset += written


def _read_exact_open_file(descriptor: int, expected_size: int) -> bytes:
    os.lseek(descriptor, 0, os.SEEK_SET)
    chunks: list[bytes] = []
    remaining = expected_size
    while remaining:
        chunk = os.read(descriptor, min(1_048_576, remaining))
        if not chunk:
            raise ProtocolFailure("artifact-invalid")
        chunks.append(chunk)
        remaining -= len(chunk)
    if os.read(descriptor, 1):
        raise ProtocolFailure("artifact-invalid")
    return b"".join(chunks)


def _write_new_artifact(filename: str, payload: bytes) -> dict[str, object]:
    if filename not in ("model.step", "preview.glb"):
        raise ProtocolFailure("internal-error")
    directory_descriptor = _open_directory_no_follow(OUTPUT_DIRECTORY)
    temporary = "." + filename + ".photon-tmp"
    write_descriptor = -1
    read_descriptor = -1
    written_metadata: os.stat_result | None = None
    temporary_created = False
    target_linked = False
    committed = False
    try:
        flags = (
            os.O_RDWR
            | os.O_CREAT
            | os.O_EXCL
            | getattr(os, "O_CLOEXEC", 0)
            | getattr(os, "O_NOFOLLOW", 0)
        )
        write_descriptor = os.open(temporary, flags, 0o600, dir_fd=directory_descriptor)
        temporary_created = True
        _write_all(write_descriptor, payload)
        os.fsync(write_descriptor)
        written_metadata = os.fstat(write_descriptor)
        if not stat.S_ISREG(written_metadata.st_mode) or written_metadata.st_size != len(payload):
            raise ProtocolFailure("operation-failed")
        os.link(
            temporary,
            filename,
            src_dir_fd=directory_descriptor,
            dst_dir_fd=directory_descriptor,
            follow_symlinks=False,
        )
        target_linked = True
        os.fsync(directory_descriptor)
        read_flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
        read_descriptor = os.open(filename, read_flags, dir_fd=directory_descriptor)
        linked_metadata = os.fstat(read_descriptor)
        if (
            linked_metadata.st_dev != written_metadata.st_dev
            or linked_metadata.st_ino != written_metadata.st_ino
            or linked_metadata.st_size != len(payload)
        ):
            raise ProtocolFailure("artifact-invalid")
        named_temporary = os.stat(temporary, dir_fd=directory_descriptor, follow_symlinks=False)
        if (
            named_temporary.st_dev != written_metadata.st_dev
            or named_temporary.st_ino != written_metadata.st_ino
        ):
            raise ProtocolFailure("artifact-invalid")
        os.unlink(temporary, dir_fd=directory_descriptor)
        temporary_created = False
        os.fsync(directory_descriptor)
        sealed_payload = _read_exact_open_file(read_descriptor, len(payload))
        final_metadata = os.fstat(read_descriptor)
        if (
            final_metadata.st_dev != written_metadata.st_dev
            or final_metadata.st_ino != written_metadata.st_ino
            or final_metadata.st_size != len(payload)
            or final_metadata.st_nlink != 1
            or sealed_payload != payload
        ):
            raise ProtocolFailure("artifact-invalid")
        committed = True
    except FileExistsError as exception:
        raise ProtocolFailure("artifact-invalid") from exception
    except ProtocolFailure:
        raise
    except Exception as exception:
        raise ProtocolFailure("operation-failed") from exception
    finally:
        if read_descriptor >= 0:
            os.close(read_descriptor)
        if write_descriptor >= 0:
            os.close(write_descriptor)
        if temporary_created:
            try:
                named_temporary = os.stat(temporary, dir_fd=directory_descriptor, follow_symlinks=False)
                if (
                    written_metadata is not None
                    and named_temporary.st_dev == written_metadata.st_dev
                    and named_temporary.st_ino == written_metadata.st_ino
                ):
                    os.unlink(temporary, dir_fd=directory_descriptor)
            except Exception:
                pass
        if not committed and target_linked and written_metadata is not None:
            try:
                target_metadata = os.stat(filename, dir_fd=directory_descriptor, follow_symlinks=False)
                if target_metadata.st_dev == written_metadata.st_dev and target_metadata.st_ino == written_metadata.st_ino:
                    os.unlink(filename, dir_fd=directory_descriptor)
            except Exception:
                pass
        try:
            os.fsync(directory_descriptor)
        except Exception:
            pass
        os.close(directory_descriptor)
    return {
        "format": "step" if filename.endswith(".step") else "glb",
        "contentDigest": _sha256(payload),
        "byteLength": len(payload),
    }


def _primitive(request: dict[str, object]) -> dict[str, object]:
    _exact_keys(request, {"schema", "operation", "primitive"})
    primitive = _require_object(request["primitive"])
    _exact_keys(primitive, {"kind", "dimensions"})
    kind = primitive["kind"]
    dimensions = _require_object(primitive["dimensions"])
    if build123d is None:
        raise ProtocolFailure("dependency-unavailable")
    if kind == "box":
        _exact_keys(dimensions, {"lengthMm", "widthMm", "heightMm"})
        length = _finite_number(dimensions["lengthMm"], 0.000001, 1_000_000.0)
        width = _finite_number(dimensions["widthMm"], 0.000001, 1_000_000.0)
        height = _finite_number(dimensions["heightMm"], 0.000001, 1_000_000.0)
        shape = build123d.Box(length, width, height)
        canonical_parameters = {"heightMm": height, "lengthMm": length, "widthMm": width}
    elif kind == "cylinder":
        _exact_keys(dimensions, {"radiusMm", "heightMm"})
        radius = _finite_number(dimensions["radiusMm"], 0.000001, 1_000_000.0)
        height = _finite_number(dimensions["heightMm"], 0.000001, 1_000_000.0)
        shape = build123d.Cylinder(radius, height)
        canonical_parameters = {"heightMm": height, "radiusMm": radius}
    else:
        raise ProtocolFailure("invalid-parameter")
    receipt, shape = _validate_shape(shape)
    payload = _step_bytes(shape)
    artifact = _write_new_artifact("model.step", payload)
    return {
        "operation": "createPrimitive",
        "artifact": artifact,
        "measurement": receipt,
        "provenance": {
            "generator": "primitive",
            "kind": kind,
            "parameters": canonical_parameters,
        },
    }


def _create_catalog_item(request: dict[str, object]) -> dict[str, object]:
    _exact_keys(request, {"schema", "operation", "catalogDigest", "itemId", "parameters"})
    supplied_digest = _expected_digest(request["catalogDigest"])
    item_id = request["itemId"]
    if not isinstance(item_id, str) or re.fullmatch(r"bdw_[0-9a-f]{48}", item_id) is None:
        raise ProtocolFailure("item-not-found")
    supplied = _require_object(request["parameters"])
    if len(supplied) > MAX_PARAMETERS:
        raise ProtocolFailure("resource-limit")
    _, runtime_items, actual_digest = _catalog()
    if supplied_digest != actual_digest:
        raise ProtocolFailure("catalog-mismatch")
    runtime_item = runtime_items.get(item_id)
    if runtime_item is None:
        raise ProtocolFailure("item-not-found")
    descriptors = typing.cast(dict[str, dict[str, object]], runtime_item["parameters"])
    converters = typing.cast(dict[str, typing.Callable[[object], object]], runtime_item["converters"])
    if not set(supplied).issubset(descriptors):
        raise ProtocolFailure("invalid-parameter")
    for name, descriptor in descriptors.items():
        if descriptor["required"] is True and name not in supplied:
            raise ProtocolFailure("invalid-parameter")
    arguments = dict(typing.cast(dict[str, object], runtime_item["fixed"]))
    for name, value in supplied.items():
        arguments[name] = converters[name](value)
    try:
        shape = typing.cast(type, runtime_item["candidate"])(**arguments)
    except Exception as exception:
        raise ProtocolFailure("operation-failed") from exception
    receipt, shape = _validate_shape(shape)
    payload = _step_bytes(shape)
    artifact = _write_new_artifact("model.step", payload)
    return {
        "operation": "createCatalogItem",
        "catalogDigest": actual_digest,
        "itemId": item_id,
        "artifact": artifact,
        "measurement": receipt,
        "provenance": {
            "generator": "catalog",
            "catalogDigest": actual_digest,
            "itemId": item_id,
            "parameters": {name: supplied[name] for name in sorted(supplied)},
        },
    }


def _read_sealed_model(slot: str, expected: str, total: list[int]) -> tuple[bytes, object, dict[str, object]]:
    input_directory_descriptor = _open_directory_no_follow(INPUT_DIRECTORY)
    filename = slot + ".step"
    descriptor = -1
    try:
        flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
        descriptor = os.open(filename, flags, dir_fd=input_directory_descriptor)
        before = os.fstat(descriptor)
        if not stat.S_ISREG(before.st_mode) or before.st_size < 1:
            raise ProtocolFailure("artifact-invalid")
        if before.st_size > MAX_MODEL_BYTES:
            raise ProtocolFailure("resource-limit")
        if before.st_nlink != 1:
            raise ProtocolFailure("artifact-invalid")
        payload = _read_exact_open_file(descriptor, before.st_size)
        after = os.fstat(descriptor)
        if (
            before.st_dev != after.st_dev
            or before.st_ino != after.st_ino
            or before.st_size != after.st_size
            or before.st_mtime_ns != after.st_mtime_ns
            or after.st_nlink != 1
        ):
            raise ProtocolFailure("artifact-invalid")
    except ProtocolFailure:
        raise
    except Exception as exception:
        raise ProtocolFailure("artifact-invalid") from exception
    finally:
        if descriptor >= 0:
            os.close(descriptor)
        os.close(input_directory_descriptor)
    total[0] += len(payload)
    if total[0] > MAX_TOTAL_INPUT_BYTES:
        raise ProtocolFailure("resource-limit")
    if _sha256(payload) != expected:
        raise ProtocolFailure("artifact-invalid")
    private_directory_descriptor = -1
    private_descriptor = -1
    private_filename = slot + ".step"
    private_path = PRIVATE_DIRECTORY / private_filename
    try:
        try:
            os.mkdir(PRIVATE_DIRECTORY, mode=0o700)
        except FileExistsError:
            pass
        private_directory_descriptor = _open_directory_no_follow(PRIVATE_DIRECTORY)
        private_directory_metadata = os.fstat(private_directory_descriptor)
        if private_directory_metadata.st_uid != os.geteuid() or private_directory_metadata.st_mode & 0o077:
            raise ProtocolFailure("artifact-invalid")
        flags = (
            os.O_WRONLY
            | os.O_CREAT
            | os.O_EXCL
            | getattr(os, "O_CLOEXEC", 0)
            | getattr(os, "O_NOFOLLOW", 0)
        )
        private_descriptor = os.open(private_filename, flags, 0o600, dir_fd=private_directory_descriptor)
        _write_all(private_descriptor, payload)
        os.fsync(private_descriptor)
        os.close(private_descriptor)
        private_descriptor = -1
        os.fsync(private_directory_descriptor)
        if build123d is None:
            raise ProtocolFailure("dependency-unavailable")
        shape = build123d.import_step(private_path)
        receipt, shape = _validate_shape(shape)
        return payload, shape, receipt
    except ProtocolFailure:
        raise
    except Exception as exception:
        raise ProtocolFailure("artifact-invalid") from exception
    finally:
        if private_descriptor >= 0:
            os.close(private_descriptor)
        if private_directory_descriptor >= 0:
            try:
                os.unlink(private_filename, dir_fd=private_directory_descriptor)
                os.fsync(private_directory_descriptor)
            except FileNotFoundError:
                pass
            except Exception:
                pass
            os.close(private_directory_descriptor)


def _matrix(value: object) -> list[float]:
    if not isinstance(value, list) or len(value) != 16:
        raise ProtocolFailure("invalid-parameter")
    matrix = [_finite_number(component, -MAX_ABSOLUTE_COORDINATE, MAX_ABSOLUTE_COORDINATE) for component in value]
    tolerance = 1e-6
    if any(abs(matrix[12 + index] - expected) > tolerance for index, expected in enumerate((0.0, 0.0, 0.0, 1.0))):
        raise ProtocolFailure("invalid-parameter")
    rotation = [[matrix[row * 4 + column] for column in range(3)] for row in range(3)]
    for row in range(3):
        norm = sum(component * component for component in rotation[row])
        if abs(norm - 1.0) > tolerance:
            raise ProtocolFailure("invalid-parameter")
        for other in range(row + 1, 3):
            dot = sum(rotation[row][index] * rotation[other][index] for index in range(3))
            if abs(dot) > tolerance:
                raise ProtocolFailure("invalid-parameter")
    determinant = (
        rotation[0][0] * (rotation[1][1] * rotation[2][2] - rotation[1][2] * rotation[2][1])
        - rotation[0][1] * (rotation[1][0] * rotation[2][2] - rotation[1][2] * rotation[2][0])
        + rotation[0][2] * (rotation[1][0] * rotation[2][1] - rotation[1][1] * rotation[2][0])
    )
    if abs(determinant - 1.0) > tolerance:
        raise ProtocolFailure("invalid-parameter")
    return [0.0 if component == 0 else component for component in matrix]


def _matrix_multiply(left: list[float], right: list[float]) -> list[float]:
    result = [0.0] * 16
    for row in range(4):
        for column in range(4):
            result[row * 4 + column] = sum(
                left[row * 4 + index] * right[index * 4 + column] for index in range(4)
            )
    return result


def _transform_point(matrix: list[float], point: tuple[float, float, float]) -> tuple[float, float, float]:
    x, y, z = point
    return (
        matrix[0] * x + matrix[1] * y + matrix[2] * z + matrix[3],
        matrix[4] * x + matrix[5] * y + matrix[6] * z + matrix[7],
        matrix[8] * x + matrix[9] * y + matrix[10] * z + matrix[11],
    )


def _column_major(matrix: list[float]) -> list[float]:
    return [matrix[row * 4 + column] for column in range(4) for row in range(4)]


def _append_binary(binary: bytearray, payload: bytes) -> tuple[int, int]:
    while len(binary) % 4:
        binary.append(0)
    offset = len(binary)
    binary.extend(payload)
    return offset, len(payload)


def _glb(request: dict[str, object]) -> dict[str, object]:
    _exact_keys(request, {"schema", "operation", "sources", "occurrences"}, {"tessellation"})
    raw_sources = request["sources"]
    raw_occurrences = request["occurrences"]
    if not isinstance(raw_sources, list) or not 1 <= len(raw_sources) <= MAX_SOURCES:
        raise ProtocolFailure("resource-limit")
    if not isinstance(raw_occurrences, list) or not 1 <= len(raw_occurrences) <= MAX_OCCURRENCES:
        raise ProtocolFailure("resource-limit")
    linear_tolerance = 0.1
    angular_tolerance = 0.1
    if "tessellation" in request:
        tessellation = _require_object(request["tessellation"])
        _exact_keys(tessellation, {"linearToleranceMm", "angularToleranceRad"})
        linear_tolerance = _finite_number(tessellation["linearToleranceMm"], 0.01, 10.0)
        angular_tolerance = _finite_number(tessellation["angularToleranceRad"], 0.01, 1.0)
    source_requests: dict[str, tuple[str, str]] = {}
    for raw_source in raw_sources:
        source = _require_object(raw_source)
        _exact_keys(source, {"sourcePartId", "inputSlot", "expectedDigest"})
        source_part_id = _safe_identifier(source["sourcePartId"])
        if source_part_id in source_requests:
            raise ProtocolFailure("invalid-request")
        source_requests[source_part_id] = (_safe_slot(source["inputSlot"]), _expected_digest(source["expectedDigest"]))
    occurrences: dict[str, dict[str, object]] = {}
    used_sources: set[str] = set()
    for raw_occurrence in raw_occurrences:
        occurrence = _require_object(raw_occurrence)
        _exact_keys(occurrence, {"entityId", "sourcePartId", "parentEntityId", "transform"})
        entity_id = _safe_identifier(occurrence["entityId"])
        source_part_id = _safe_identifier(occurrence["sourcePartId"])
        parent = occurrence["parentEntityId"]
        if parent is not None:
            parent = _safe_identifier(parent)
        if entity_id in occurrences or source_part_id not in source_requests or parent == entity_id:
            raise ProtocolFailure("invalid-request")
        occurrences[entity_id] = {
            "entityId": entity_id,
            "sourcePartId": source_part_id,
            "parentEntityId": parent,
            "transform": _matrix(occurrence["transform"]),
        }
        used_sources.add(source_part_id)
    if used_sources != set(source_requests):
        raise ProtocolFailure("invalid-request")
    ordered_entities = sorted(occurrences)
    entity_indexes = {entity_id: index for index, entity_id in enumerate(ordered_entities)}
    children: dict[str, list[str]] = {entity_id: [] for entity_id in ordered_entities}
    roots: list[str] = []
    for entity_id in ordered_entities:
        parent = occurrences[entity_id]["parentEntityId"]
        if parent is None:
            roots.append(entity_id)
        elif parent not in occurrences:
            raise ProtocolFailure("invalid-request")
        else:
            children[typing.cast(str, parent)].append(entity_id)
    if not roots:
        raise ProtocolFailure("invalid-request")
    identity_matrix = [
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
        0.0, 0.0, 1.0, 0.0,
        0.0, 0.0, 0.0, 1.0,
    ]
    world_matrices: dict[str, list[float]] = {}
    pending: list[tuple[str, int, list[float]]] = [(root, 1, identity_matrix) for root in sorted(roots)]
    cursor = 0
    while cursor < len(pending):
        entity_id, depth, parent_world = pending[cursor]
        cursor += 1
        if depth > MAX_DAG_DEPTH or entity_id in world_matrices:
            raise ProtocolFailure("resource-limit")
        local = typing.cast(list[float], occurrences[entity_id]["transform"])
        world = _matrix_multiply(parent_world, local)
        world_matrices[entity_id] = world
        for child in sorted(children[entity_id]):
            pending.append((child, depth + 1, world))
    if len(world_matrices) != len(occurrences):
        raise ProtocolFailure("invalid-request")

    total_input = [0]
    imported: dict[str, dict[str, object]] = {}
    for source_id in sorted(source_requests):
        slot, expected = source_requests[source_id]
        payload, shape, measurement = _read_sealed_model(slot, expected, total_input)
        try:
            vertices, triangles = shape.tessellate(linear_tolerance, angular_tolerance)
        except Exception as exception:
            raise ProtocolFailure("operation-failed") from exception
        if not vertices or not triangles or len(triangles) > MAX_TRIANGLES:
            raise ProtocolFailure("resource-limit")
        imported[source_id] = {
            "vertices": vertices,
            "triangles": triangles,
            "measurement": measurement,
            "contentDigest": expected,
            "byteLength": len(payload),
        }

    binary = bytearray()
    buffer_views: list[dict[str, object]] = []
    accessors: list[dict[str, object]] = []
    meshes: list[dict[str, object]] = []
    mesh_indexes: dict[str, int] = {}
    total_accessor_elements = 0
    for source_id in sorted(imported):
        vertices = typing.cast(list[object], imported[source_id]["vertices"])
        triangles = typing.cast(list[tuple[int, int, int]], imported[source_id]["triangles"])
        if len(vertices) > 5_000_000:
            raise ProtocolFailure("resource-limit")
        positions: list[tuple[float, float, float]] = []
        for vertex in vertices:
            coordinates = (float(vertex.X), float(vertex.Y), float(vertex.Z))
            if any(not math.isfinite(value) or abs(value) > MAX_ABSOLUTE_COORDINATE for value in coordinates):
                raise ProtocolFailure("artifact-invalid")
            positions.append(coordinates)
        flattened_indices: list[int] = []
        for triangle in triangles:
            if len(triangle) != 3 or any(type(index) is not int or index < 0 or index >= len(positions) for index in triangle):
                raise ProtocolFailure("artifact-invalid")
            flattened_indices.extend(triangle)
        total_accessor_elements += len(positions) + len(flattened_indices)
        if total_accessor_elements > MAX_ACCESSOR_ELEMENTS:
            raise ProtocolFailure("resource-limit")
        position_payload = b"".join(struct.pack("<fff", *position) for position in positions)
        index_payload = b"".join(struct.pack("<I", index) for index in flattened_indices)
        position_offset, position_length = _append_binary(binary, position_payload)
        position_view = len(buffer_views)
        buffer_views.append({"buffer": 0, "byteOffset": position_offset, "byteLength": position_length, "target": 34962})
        index_offset, index_length = _append_binary(binary, index_payload)
        index_view = len(buffer_views)
        buffer_views.append({"buffer": 0, "byteOffset": index_offset, "byteLength": index_length, "target": 34963})
        minimum = [min(position[axis] for position in positions) for axis in range(3)]
        maximum = [max(position[axis] for position in positions) for axis in range(3)]
        position_accessor = len(accessors)
        accessors.append(
            {
                "bufferView": position_view,
                "byteOffset": 0,
                "componentType": 5126,
                "count": len(positions),
                "type": "VEC3",
                "min": minimum,
                "max": maximum,
            }
        )
        index_accessor = len(accessors)
        accessors.append(
            {
                "bufferView": index_view,
                "byteOffset": 0,
                "componentType": 5125,
                "count": len(flattened_indices),
                "type": "SCALAR",
                "min": [min(flattened_indices)],
                "max": [max(flattened_indices)],
            }
        )
        mesh_indexes[source_id] = len(meshes)
        meshes.append({"primitives": [{"attributes": {"POSITION": position_accessor}, "indices": index_accessor, "mode": 4}]})
    if len(binary) > MAX_GLB_BYTES:
        raise ProtocolFailure("resource-limit")

    nodes: list[dict[str, object]] = []
    overall_minimum = [math.inf, math.inf, math.inf]
    overall_maximum = [-math.inf, -math.inf, -math.inf]
    for entity_id in ordered_entities:
        occurrence = occurrences[entity_id]
        source_id = typing.cast(str, occurrence["sourcePartId"])
        local = typing.cast(list[float], occurrence["transform"])
        node: dict[str, object] = {
            "mesh": mesh_indexes[source_id],
            "matrix": _column_major(local),
            "extras": {"photonEntityId": entity_id},
        }
        if children[entity_id]:
            node["children"] = [entity_indexes[child] for child in sorted(children[entity_id])]
        nodes.append(node)
        measurement = typing.cast(dict[str, object], imported[source_id]["measurement"])
        bounds = typing.cast(dict[str, list[float]], measurement["bounds"])
        minimum = bounds["minimum"]
        maximum = bounds["maximum"]
        world = world_matrices[entity_id]
        for x in (minimum[0], maximum[0]):
            for y in (minimum[1], maximum[1]):
                for z in (minimum[2], maximum[2]):
                    point = _transform_point(world, (x, y, z))
                    for axis in range(3):
                        overall_minimum[axis] = min(overall_minimum[axis], point[axis])
                        overall_maximum[axis] = max(overall_maximum[axis], point[axis])
    if any(not math.isfinite(value) or abs(value) > MAX_ABSOLUTE_COORDINATE for value in overall_minimum + overall_maximum):
        raise ProtocolFailure("artifact-invalid")
    document: dict[str, object] = {
        "asset": {"version": "2.0", "generator": "Photon CAD industrial container"},
        "scene": 0,
        "scenes": [{"nodes": [entity_indexes[root] for root in sorted(roots)]}],
        "nodes": nodes,
        "meshes": meshes,
        "buffers": [{"byteLength": len(binary)}],
        "bufferViews": buffer_views,
        "accessors": accessors,
    }
    json_payload = _canonical_json(document)
    if len(json_payload) > 32 * 1024 * 1024:
        raise ProtocolFailure("resource-limit")
    while len(json_payload) % 4:
        json_payload += b" "
    while len(binary) % 4:
        binary.append(0)
    total_length = 12 + 8 + len(json_payload) + 8 + len(binary)
    if total_length > MAX_GLB_BYTES:
        raise ProtocolFailure("resource-limit")
    glb = (
        struct.pack("<III", 0x46546C67, 2, total_length)
        + struct.pack("<II", len(json_payload), 0x4E4F534A)
        + json_payload
        + struct.pack("<II", len(binary), 0x004E4942)
        + bytes(binary)
    )
    artifact = _write_new_artifact("preview.glb", glb)
    return {
        "operation": "createPreview",
        "artifact": artifact,
        "entityCount": len(ordered_entities),
        "sourceCount": len(source_requests),
        "bounds": {"minimum": overall_minimum, "maximum": overall_maximum},
        "units": "millimeter",
        "provenance": {
            "sources": [
                {
                    "sourcePartId": source_id,
                    "contentDigest": typing.cast(str, imported[source_id]["contentDigest"]),
                    "byteLength": typing.cast(int, imported[source_id]["byteLength"]),
                }
                for source_id in sorted(imported)
            ],
            "occurrences": [
                {
                    "entityId": entity_id,
                    "sourcePartId": typing.cast(str, occurrences[entity_id]["sourcePartId"]),
                    "parentEntityId": occurrences[entity_id]["parentEntityId"],
                    "transform": typing.cast(list[float], occurrences[entity_id]["transform"]),
                }
                for entity_id in ordered_entities
            ],
            "tessellation": {
                "linearToleranceMm": linear_tolerance,
                "angularToleranceRad": angular_tolerance,
            },
        },
    }


def _dispatch(request: dict[str, object]) -> dict[str, object]:
    if request.get("schema") != REQUEST_SCHEMA or not isinstance(request.get("operation"), str):
        raise ProtocolFailure("invalid-request")
    if not DEPENDENCIES_READY:
        raise ProtocolFailure("dependency-unavailable")
    operation = request["operation"]
    if operation == "catalog":
        _exact_keys(request, {"schema", "operation"})
        catalog, _, digest = _catalog()
        return {"operation": "catalog", "catalogDigest": digest, "catalog": catalog}
    if operation == "createPrimitive":
        return _primitive(request)
    if operation == "createCatalogItem":
        return _create_catalog_item(request)
    if operation == "createPreview":
        return _glb(request)
    raise ProtocolFailure("unsupported-operation")


def _emit(response: dict[str, object]) -> None:
    payload = _canonical_json(response)
    if len(payload) > MAX_RESPONSE_BYTES:
        payload = _canonical_json({"schema": RESPONSE_SCHEMA, "ok": False, "error": "resource-limit"})
    sys.stdout.buffer.write(payload + b"\n")
    sys.stdout.buffer.flush()


def _timeout_handler(_signal_number: int, _frame: object) -> None:
    raise OperationTimedOut


@contextlib.contextmanager
def _silence_dependency_output() -> typing.Iterator[None]:
    saved_stdout = -1
    saved_stderr = -1
    null_descriptor = -1
    try:
        sys.stdout.flush()
        sys.stderr.flush()
        saved_stdout = os.dup(sys.stdout.fileno())
        saved_stderr = os.dup(sys.stderr.fileno())
        null_descriptor = os.open(os.devnull, os.O_WRONLY | getattr(os, "O_CLOEXEC", 0))
        os.dup2(null_descriptor, sys.stdout.fileno())
        os.dup2(null_descriptor, sys.stderr.fileno())
        yield
    finally:
        try:
            sys.stdout.flush()
            sys.stderr.flush()
        except Exception:
            pass
        if saved_stdout >= 0:
            os.dup2(saved_stdout, sys.stdout.fileno())
            os.close(saved_stdout)
        if saved_stderr >= 0:
            os.dup2(saved_stderr, sys.stderr.fileno())
            os.close(saved_stderr)
        if null_descriptor >= 0:
            os.close(null_descriptor)


def main() -> int:
    try:
        signal.signal(signal.SIGALRM, _timeout_handler)
        signal.setitimer(signal.ITIMER_REAL, OPERATION_TIMEOUT_SECONDS)
        payload = sys.stdin.buffer.read(MAX_REQUEST_BYTES + 1)
        if len(payload) > MAX_REQUEST_BYTES:
            raise ProtocolFailure("request-too-large")
        request = _strict_json(payload)
        with _silence_dependency_output():
            result = _dispatch(_require_object(request))
        _emit({"schema": RESPONSE_SCHEMA, "ok": True, **result})
        return 0
    except OperationTimedOut:
        _emit({"schema": RESPONSE_SCHEMA, "ok": False, "error": "timeout"})
        return 2
    except ProtocolFailure as failure:
        _emit({"schema": RESPONSE_SCHEMA, "ok": False, "error": failure.code})
        return 2
    except MemoryError:
        _emit({"schema": RESPONSE_SCHEMA, "ok": False, "error": "resource-limit"})
        return 2
    except Exception:
        _emit({"schema": RESPONSE_SCHEMA, "ok": False, "error": "internal-error"})
        return 2
    finally:
        try:
            signal.setitimer(signal.ITIMER_REAL, 0)
        except Exception:
            pass


if __name__ == "__main__":
    raise SystemExit(main())
