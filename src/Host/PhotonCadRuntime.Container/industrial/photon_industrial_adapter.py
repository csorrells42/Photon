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
    from OCP.IFSelect import IFSelect_RetDone
    from OCP.STEPCAFControl import STEPCAFControl_Reader
    from OCP.STEPControl import STEPControl_AsIs, STEPControl_Writer
    from OCP.TCollection import TCollection_AsciiString, TCollection_ExtendedString
    from OCP.TDataStd import TDataStd_Name
    from OCP.TDF import TDF_Label, TDF_LabelSequence, TDF_Tool
    from OCP.TDocStd import TDocStd_Document
    from OCP.XCAFDoc import XCAFDoc_DocumentTool, XCAFDoc_ShapeTool

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
    IFSelect_RetDone = None
    STEPCAFControl_Reader = None
    STEPControl_AsIs = None
    STEPControl_Writer = None
    TCollection_AsciiString = None
    TCollection_ExtendedString = None
    TDataStd_Name = None
    TDF_Label = None
    TDF_LabelSequence = None
    TDF_Tool = None
    TDocStd_Document = None
    XCAFDoc_DocumentTool = None
    XCAFDoc_ShapeTool = None
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
    if filename not in ("model.step", "preview.glb") and re.fullmatch(r"definition-[0-9]{4}\.step", filename) is None:
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


def _manual_profile_solid(profile_request: object, depth_value: object) -> tuple[object, dict[str, object], float]:
    depth = _finite_number(depth_value, 0.000001, 1_000_000.0)
    profile = _require_object(profile_request)
    kind = profile.get("kind")
    if build123d is None:
        raise ProtocolFailure("dependency-unavailable")
    try:
        with build123d.BuildPart() as part:
            if "plane" in profile:
                if profile.get("plane") != "xy":
                    raise ProtocolFailure("invalid-parameter")
                plane = build123d.Plane.XY
                frame: dict[str, object] | None = None
            else:
                _exact_keys(profile, {"kind", "originMm", "xDirection", "normal", "points"}
                            | ({"cornerRadiiMm"} if kind == "filletedPolygon" else set()))
                origin = _manual_vector(profile["originMm"])
                x_direction = _manual_unit_vector(profile["xDirection"])
                normal = _manual_unit_vector(profile["normal"])
                if abs(sum(x_direction[index] * normal[index] for index in range(3))) > 1e-8:
                    raise ProtocolFailure("invalid-parameter")
                plane = build123d.Plane(origin=origin, x_dir=x_direction, z_dir=normal)
                frame = {
                    "originMm": {"x": origin[0], "y": origin[1], "z": origin[2]},
                    "xDirection": {"x": x_direction[0], "y": x_direction[1], "z": x_direction[2]},
                    "normal": {"x": normal[0], "y": normal[1], "z": normal[2]},
                }
            with build123d.BuildSketch(plane):
                if kind == "rectangle":
                    if frame is None:
                        _exact_keys(profile, {"kind", "plane", "widthMm", "heightMm"})
                        width = _finite_number(profile["widthMm"], 0.000001, 1_000_000.0)
                        height = _finite_number(profile["heightMm"], 0.000001, 1_000_000.0)
                        build123d.Rectangle(width, height)
                        provenance: dict[str, object] = {"heightMm": height, "kind": "rectangle", "plane": "xy", "widthMm": width}
                    else:
                        points = _manual_profile_points(profile["points"], 2)
                        width = abs(points[1][0] - points[0][0])
                        height = abs(points[1][1] - points[0][1])
                        if width <= 0 or height <= 0:
                            raise ProtocolFailure("invalid-parameter")
                        center = ((points[1][0] + points[0][0]) / 2, (points[1][1] + points[0][1]) / 2)
                        with build123d.Locations(center):
                            build123d.Rectangle(width, height)
                        provenance = {"kind": "rectangle", "points": _manual_points_provenance(points), **frame}
                elif kind == "circle":
                    if frame is None:
                        _exact_keys(profile, {"kind", "plane", "radiusMm"})
                        radius = _finite_number(profile["radiusMm"], 0.000001, 1_000_000.0)
                        build123d.Circle(radius)
                        provenance = {"kind": "circle", "plane": "xy", "radiusMm": radius}
                    else:
                        points = _manual_profile_points(profile["points"], 2)
                        radius = math.dist(points[0], points[1])
                        if radius <= 0:
                            raise ProtocolFailure("invalid-parameter")
                        with build123d.Locations(points[0]):
                            build123d.Circle(radius)
                        provenance = {"kind": "circle", "points": _manual_points_provenance(points), **frame}
                elif kind in {"polygon", "filletedPolygon"} and frame is not None:
                    points = _manual_profile_points(profile["points"], 3)
                    if len(points) > 64 or abs(sum(points[index][0] * points[(index + 1) % len(points)][1] - points[(index + 1) % len(points)][0] * points[index][1] for index in range(len(points)))) <= 1e-8:
                        raise ProtocolFailure("invalid-parameter")
                    if kind == "filletedPolygon":
                        radii = _manual_corner_radii(profile["cornerRadiiMm"], len(points))
                        with build123d.BuildLine():
                            build123d.FilletPolyline(*points, radius=radii, close=True)
                        build123d.make_face()
                        provenance = {
                            "kind": "filletedPolygon",
                            "points": _manual_points_provenance(points),
                            "cornerRadiiMm": radii,
                            **frame,
                        }
                    else:
                        build123d.Polygon(*points)
                        provenance = {"kind": "polygon", "points": _manual_points_provenance(points), **frame}
                else:
                    raise ProtocolFailure("invalid-parameter")
            build123d.extrude(amount=depth)
        return _validate_shape(part.part)[1], provenance, depth
    except ProtocolFailure:
        raise
    except Exception as exception:
        raise ProtocolFailure("operation-failed") from exception


def _manual_vector(value: object) -> tuple[float, float, float]:
    vector = _require_object(value)
    _exact_keys(vector, {"x", "y", "z"})
    return (
        _finite_number(vector["x"], -1_000_000.0, 1_000_000.0),
        _finite_number(vector["y"], -1_000_000.0, 1_000_000.0),
        _finite_number(vector["z"], -1_000_000.0, 1_000_000.0),
    )


def _manual_unit_vector(value: object) -> tuple[float, float, float]:
    vector = _manual_vector(value)
    length = math.sqrt(sum(component * component for component in vector))
    if not math.isfinite(length) or abs(length - 1.0) > 1e-6:
        raise ProtocolFailure("invalid-parameter")
    return vector


def _manual_profile_points(value: object, minimum: int) -> list[tuple[float, float]]:
    if not isinstance(value, list) or len(value) < minimum or len(value) > 64:
        raise ProtocolFailure("invalid-parameter")
    points: list[tuple[float, float]] = []
    for raw in value:
        point = _require_object(raw)
        _exact_keys(point, {"x", "y"})
        candidate = (_finite_number(point["x"], -1_000_000.0, 1_000_000.0), _finite_number(point["y"], -1_000_000.0, 1_000_000.0))
        if candidate in points:
            raise ProtocolFailure("invalid-parameter")
        points.append(candidate)
    return points


def _manual_corner_radii(value: object, expected_count: int) -> list[float]:
    if not isinstance(value, list) or len(value) != expected_count:
        raise ProtocolFailure("invalid-parameter")
    radii = [_finite_number(raw, 0.0, 1_000_000.0) for raw in value]
    if not any(radius > 0 for radius in radii):
        raise ProtocolFailure("invalid-parameter")
    return radii


def _manual_points_provenance(points: list[tuple[float, float]]) -> list[dict[str, float]]:
    return [{"x": point[0], "y": point[1]} for point in points]


def _manual_response(operation: str, shape: object, provenance: dict[str, object]) -> dict[str, object]:
    receipt, shape = _validate_shape(shape)
    payload = _step_bytes(shape)
    artifact = _write_new_artifact("model.step", payload)
    return {
        "operation": operation,
        "artifact": artifact,
        "measurement": receipt,
        "provenance": provenance,
    }


def _manual_sketch_extrude_add(request: dict[str, object]) -> dict[str, object]:
    expected = {"schema", "operation", "profile", "depthMm"}
    if "source" in request:
        expected.add("source")
    _exact_keys(request, expected)
    shape, profile, depth = _manual_profile_solid(request["profile"], request["depthMm"])
    if "source" in request:
        base, base_receipt = _manual_source(request)
        try:
            shape = base.fuse(shape)
        except Exception as exception:
            raise ProtocolFailure("operation-failed") from exception
        return _manual_response(
            "manualSketchExtrudeAdd",
            shape,
            {"generator": "manual", "kind": "sketchExtrudeAdd", "profile": profile,
             "depthMm": depth, "sourceVolumeMm3": base_receipt["volumeMm3"]},
        )
    return _manual_response(
        "manualSketchExtrudeAdd",
        shape,
        {"generator": "manual", "kind": "sketchExtrudeAdd", "profile": profile, "depthMm": depth},
    )


def _manual_source(request: dict[str, object]) -> tuple[object, dict[str, object]]:
    source = _require_object(request["source"])
    _exact_keys(source, {"inputSlot", "expectedDigest"})
    _payload, shape, receipt = _read_sealed_model(
        _safe_slot(source["inputSlot"]), _expected_digest(source["expectedDigest"]), [0]
    )
    return shape, receipt


def _manual_sketch_extrude_cut(request: dict[str, object]) -> dict[str, object]:
    _exact_keys(request, {"schema", "operation", "source", "profile", "depthMm"})
    base, base_receipt = _manual_source(request)
    cutter, profile, depth = _manual_profile_solid(request["profile"], request["depthMm"])
    try:
        result = base.cut(cutter)
    except Exception as exception:
        raise ProtocolFailure("operation-failed") from exception
    return _manual_response(
        "manualSketchExtrudeCut",
        result,
        {
            "generator": "manual",
            "kind": "sketchExtrudeCut",
            "profile": profile,
            "depthMm": depth,
            "sourceVolumeMm3": base_receipt["volumeMm3"],
        },
    )


def _manual_hole_cut(request: dict[str, object]) -> dict[str, object]:
    _exact_keys(request, {"schema", "operation", "source", "hole"})
    base, base_receipt = _manual_source(request)
    hole = _require_object(request["hole"])
    _exact_keys(hole, {"radiusMm", "depthMm", "xMm", "yMm", "zMm"})
    radius = _finite_number(hole["radiusMm"], 0.000001, 1_000_000.0)
    depth = _finite_number(hole["depthMm"], 0.000001, 1_000_000.0)
    x = _finite_number(hole["xMm"], -1_000_000.0, 1_000_000.0)
    y = _finite_number(hole["yMm"], -1_000_000.0, 1_000_000.0)
    z = _finite_number(hole["zMm"], -1_000_000.0, 1_000_000.0)
    try:
        cutter = build123d.Cylinder(radius, depth)
        translated = cutter.translate((x, y, z))
        if translated is not None:
            cutter = translated
        result = base.cut(cutter)
    except Exception as exception:
        raise ProtocolFailure("operation-failed") from exception
    return _manual_response(
        "manualHoleCut",
        result,
        {
            "generator": "manual",
            "kind": "holeCut",
            "hole": {"depthMm": depth, "radiusMm": radius, "xMm": x, "yMm": y, "zMm": z},
            "sourceVolumeMm3": base_receipt["volumeMm3"],
        },
    )


def _manual_seed_cutter(seed_request: object, offset: tuple[float, float, float], angle_degrees: float) -> tuple[object, str]:
    seed = _require_object(seed_request)
    kind = seed.get("kind")
    if kind == "sketchExtrudeCut":
        _exact_keys(seed, {"kind", "profile", "depthMm"})
        cutter, _profile, _depth = _manual_profile_solid(seed["profile"], seed["depthMm"])
        if angle_degrees != 0.0:
            try:
                rotated = cutter.rotate(build123d.Axis.Z, angle_degrees)
                if rotated is not None:
                    cutter = rotated
            except Exception as exception:
                raise ProtocolFailure("operation-failed") from exception
        translated = cutter.translate(offset)
        if translated is not None:
            cutter = translated
        return cutter, kind
    if kind == "holeCut":
        _exact_keys(seed, {"kind", "radiusMm", "depthMm", "xMm", "yMm", "zMm"})
        radius = _finite_number(seed["radiusMm"], 0.000001, 1_000_000.0)
        depth = _finite_number(seed["depthMm"], 0.000001, 1_000_000.0)
        x = _finite_number(seed["xMm"], -1_000_000.0, 1_000_000.0)
        y = _finite_number(seed["yMm"], -1_000_000.0, 1_000_000.0)
        z = _finite_number(seed["zMm"], -1_000_000.0, 1_000_000.0)
        radians = math.radians(angle_degrees)
        rotated_x = x * math.cos(radians) - y * math.sin(radians)
        rotated_y = x * math.sin(radians) + y * math.cos(radians)
        try:
            cutter = build123d.Cylinder(radius, depth)
            translated = cutter.translate((rotated_x + offset[0], rotated_y + offset[1], z + offset[2]))
            if translated is not None:
                cutter = translated
        except Exception as exception:
            raise ProtocolFailure("operation-failed") from exception
        return cutter, kind
    raise ProtocolFailure("invalid-parameter")


def _manual_pattern(request: dict[str, object], circular: bool) -> dict[str, object]:
    expected = {"schema", "operation", "source", "seed", "count", "angleDegrees" if circular else "spacingMm"}
    _exact_keys(request, expected)
    base, base_receipt = _manual_source(request)
    count_value = request["count"]
    if isinstance(count_value, bool) or not isinstance(count_value, int) or count_value < 2 or count_value > 256:
        raise ProtocolFailure("invalid-parameter")
    count = count_value
    distance_name = "angleDegrees" if circular else "spacingMm"
    distance = _finite_number(request[distance_name], 0.000001, 360.0 if circular else 1_000_000.0)
    seed = _require_object(request["seed"])
    if circular:
        if seed.get("kind") != "holeCut":
            raise ProtocolFailure("invalid-parameter")
        x = _finite_number(seed.get("xMm"), -1_000_000.0, 1_000_000.0)
        y = _finite_number(seed.get("yMm"), -1_000_000.0, 1_000_000.0)
        if x == 0.0 and y == 0.0:
            raise ProtocolFailure("invalid-parameter")
    result = base
    seed_kind = ""
    try:
        for index in range(1, count):
            angle_divisor = count if distance == 360.0 else count - 1
            angle = distance * index / angle_divisor if circular else 0.0
            offset = (0.0, 0.0, 0.0) if circular else (distance * index, 0.0, 0.0)
            cutter, seed_kind = _manual_seed_cutter(request["seed"], offset, angle)
            result = result.cut(cutter)
    except ProtocolFailure:
        raise
    except Exception as exception:
        raise ProtocolFailure("operation-failed") from exception
    operation = "manualCircularPattern" if circular else "manualLinearPattern"
    kind = "circularPattern" if circular else "linearPattern"
    return _manual_response(
        operation,
        result,
        {
            "generator": "manual",
            "kind": kind,
            "seedKind": seed_kind,
            "count": count,
            distance_name: distance,
            "sourceVolumeMm3": base_receipt["volumeMm3"],
        },
    )


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


@contextlib.contextmanager
def _private_step_payload(filename: str, payload: bytes) -> typing.Iterator[Path]:
    if SAFE_SLOT.fullmatch(filename.removesuffix(".step")) is None:
        raise ProtocolFailure("invalid-parameter")
    directory_descriptor = -1
    descriptor = -1
    path = PRIVATE_DIRECTORY / filename
    try:
        try:
            os.mkdir(PRIVATE_DIRECTORY, mode=0o700)
        except FileExistsError:
            pass
        directory_descriptor = _open_directory_no_follow(PRIVATE_DIRECTORY)
        metadata = os.fstat(directory_descriptor)
        if metadata.st_uid != os.geteuid() or metadata.st_mode & 0o077:
            raise ProtocolFailure("artifact-invalid")
        flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
        descriptor = os.open(filename, flags, 0o600, dir_fd=directory_descriptor)
        _write_all(descriptor, payload)
        os.fsync(descriptor)
        os.close(descriptor)
        descriptor = -1
        os.fsync(directory_descriptor)
        yield path
    except ProtocolFailure:
        raise
    except Exception as exception:
        raise ProtocolFailure("artifact-invalid") from exception
    finally:
        if descriptor >= 0:
            os.close(descriptor)
        if directory_descriptor >= 0:
            try:
                os.unlink(filename, dir_fd=directory_descriptor)
                os.fsync(directory_descriptor)
            except FileNotFoundError:
                pass
            except Exception:
                pass
            os.close(directory_descriptor)


def _xcaf_entry(label: object) -> str:
    value = TCollection_AsciiString()
    TDF_Tool.Entry_s(label, value)
    return typing.cast(str, value.ToCString())


def _xcaf_name(label: object, fallback: str) -> str:
    attribute = TDataStd_Name()
    value = attribute.Get().ToExtString() if label.FindAttribute(TDataStd_Name.GetID_s(), attribute) else fallback
    value = " ".join(str(value).split())
    value = "".join(character if not ord(character) < 32 else "-" for character in value).strip()
    return value[:256] or fallback


def _xcaf_matrix(location: object) -> list[float]:
    transform = location.Transformation()
    return _matrix(
        [transform.Value(row, column) for row in range(1, 4) for column in range(1, 5)]
        + [0.0, 0.0, 0.0, 1.0]
    )


def _xcaf_step_bytes(shape: object, ordinal: int) -> bytes:
    directory_descriptor = -1
    descriptor = -1
    filename = f"xcaf-definition-{ordinal:04d}.step"
    path = PRIVATE_DIRECTORY / filename
    try:
        try:
            os.mkdir(PRIVATE_DIRECTORY, mode=0o700)
        except FileExistsError:
            pass
        directory_descriptor = _open_directory_no_follow(PRIVATE_DIRECTORY)
        metadata = os.fstat(directory_descriptor)
        if metadata.st_uid != os.geteuid() or metadata.st_mode & 0o077:
            raise ProtocolFailure("artifact-invalid")
        writer = STEPControl_Writer()
        if writer.Transfer(shape, STEPControl_AsIs) != IFSelect_RetDone or writer.Write(str(path)) != IFSelect_RetDone:
            raise ProtocolFailure("operation-failed")
        flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
        descriptor = os.open(filename, flags, dir_fd=directory_descriptor)
        file_metadata = os.fstat(descriptor)
        if not stat.S_ISREG(file_metadata.st_mode) or file_metadata.st_nlink != 1 or file_metadata.st_size < 1:
            raise ProtocolFailure("artifact-invalid")
        payload = _read_exact_open_file(descriptor, file_metadata.st_size)
        imported = build123d.import_step(path)
        _validate_shape(imported)
        return payload
    except ProtocolFailure:
        raise
    except Exception as exception:
        raise ProtocolFailure("artifact-invalid") from exception
    finally:
        if descriptor >= 0:
            os.close(descriptor)
        if directory_descriptor >= 0:
            try:
                os.unlink(filename, dir_fd=directory_descriptor)
                os.fsync(directory_descriptor)
            except FileNotFoundError:
                pass
            except Exception:
                pass
            os.close(directory_descriptor)


def _inspect_step_assembly(request: dict[str, object]) -> dict[str, object]:
    _exact_keys(request, {"schema", "operation", "source"})
    source = _require_object(request["source"])
    _exact_keys(source, {"inputSlot", "expectedDigest"})
    slot = _safe_slot(source["inputSlot"])
    expected = _expected_digest(source["expectedDigest"])
    payload, _, _ = _read_sealed_model(slot, expected, [0])
    if any(
        dependency is None
        for dependency in (
            STEPCAFControl_Reader,
            TCollection_ExtendedString,
            TDocStd_Document,
            XCAFDoc_DocumentTool,
            XCAFDoc_ShapeTool,
        )
    ):
        raise ProtocolFailure("dependency-unavailable")

    with _private_step_payload("assembly.step", payload) as private_path:
        try:
            document = TDocStd_Document(TCollection_ExtendedString("XmlXCAF"))
            reader = STEPCAFControl_Reader()
            reader.SetNameMode(True)
            if reader.ReadFile(str(private_path)) != IFSelect_RetDone or not reader.Transfer(document):
                raise ProtocolFailure("artifact-invalid")
            shape_tool = XCAFDoc_DocumentTool.ShapeTool_s(document.Main())
            free_shapes = TDF_LabelSequence()
            shape_tool.GetFreeShapes(free_shapes)
        except ProtocolFailure:
            raise
        except Exception as exception:
            raise ProtocolFailure("artifact-invalid") from exception

        if free_shapes.Length() < 1 or free_shapes.Length() > MAX_SOURCES:
            raise ProtocolFailure("resource-limit")

        definitions: list[dict[str, object]] = []
        occurrences: list[dict[str, object]] = []
        definition_by_entry: dict[str, dict[str, object]] = {}
        def define(label: object, parent_entity_id: str | None, original: bool = False) -> dict[str, object]:
            entry = _xcaf_entry(label)
            if entry in definition_by_entry:
                return definition_by_entry[entry]
            if len(definitions) >= MAX_SOURCES:
                raise ProtocolFailure("resource-limit")
            assembly = bool(XCAFDoc_ShapeTool.IsAssembly_s(label))
            ordinal = len(definitions) + 1
            entity_id = ("assembly" if assembly else "part") + f"-{ordinal:04d}"
            display_name = _xcaf_name(label, "Imported assembly" if assembly else f"Imported part {ordinal}")
            part_number = ("STEP-ASM" if assembly else "STEP-PART") + f"-{ordinal:04d}"
            definition: dict[str, object] = {
                "entityId": entity_id,
                "parentEntityId": parent_entity_id,
                "kind": "assembly" if assembly else "part",
                "partNumber": part_number,
                "displayName": display_name,
                "previewSource": not assembly,
            }
            if original:
                definition["artifact"] = {
                    "format": "step",
                    "contentDigest": expected,
                    "byteLength": len(payload),
                }
                definition["outputFile"] = None
            else:
                try:
                    artifact_payload = _xcaf_step_bytes(shape_tool.GetShape_s(label), ordinal)
                except ProtocolFailure:
                    raise
                except Exception as exception:
                    raise ProtocolFailure("artifact-invalid") from exception
                output_file = f"definition-{ordinal:04d}.step"
                definition["artifact"] = _write_new_artifact(output_file, artifact_payload)
                definition["outputFile"] = output_file
            definitions.append(definition)
            definition_by_entry[entry] = definition
            return definition

        one_assembly_root = free_shapes.Length() == 1 and bool(XCAFDoc_ShapeTool.IsAssembly_s(free_shapes.Value(1)))
        if one_assembly_root:
            root_label = free_shapes.Value(1)
            root_definition = define(root_label, None, original=True)
        else:
            root_definition = {
                "entityId": "assembly-0001",
                "parentEntityId": None,
                "kind": "assembly",
                "partNumber": "STEP-ASM-0001",
                "displayName": "Imported STEP assembly",
                "previewSource": False,
                "artifact": {"format": "step", "contentDigest": expected, "byteLength": len(payload)},
                "outputFile": None,
            }
            definitions.append(root_definition)
            root_label = None

        root_occurrence = {
            "occurrenceId": "occurrence-0001",
            "parentOccurrenceId": None,
            "sourceEntityId": root_definition["entityId"],
            "partNumber": root_definition["partNumber"],
            "transform": [1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0],
        }
        occurrences.append(root_occurrence)

        def append_children(
            definition_label: object,
            parent_definition_id: str,
            parent_occurrence_id: str,
            depth: int,
        ) -> None:
            if depth > MAX_DAG_DEPTH:
                raise ProtocolFailure("resource-limit")
            components = TDF_LabelSequence()
            XCAFDoc_ShapeTool.GetComponents_s(definition_label, components, False)
            for index in range(1, components.Length() + 1):
                if len(occurrences) >= MAX_OCCURRENCES:
                    raise ProtocolFailure("resource-limit")
                component = components.Value(index)
                referred = TDF_Label()
                if not XCAFDoc_ShapeTool.GetReferredShape_s(component, referred):
                    raise ProtocolFailure("artifact-invalid")
                child = define(referred, parent_definition_id)
                occurrence_id = f"occurrence-{len(occurrences) + 1:04d}"
                occurrences.append(
                    {
                        "occurrenceId": occurrence_id,
                        "parentOccurrenceId": parent_occurrence_id,
                        "sourceEntityId": child["entityId"],
                        "partNumber": child["partNumber"],
                        "transform": _xcaf_matrix(XCAFDoc_ShapeTool.GetLocation_s(component)),
                    }
                )
                if child["kind"] == "assembly":
                    append_children(referred, typing.cast(str, child["entityId"]), occurrence_id, depth + 1)

        if root_label is not None:
            append_children(
                root_label,
                typing.cast(str, root_definition["entityId"]),
                typing.cast(str, root_occurrence["occurrenceId"]),
                1,
            )
        else:
            for index in range(1, free_shapes.Length() + 1):
                free_label = free_shapes.Value(index)
                child = define(free_label, typing.cast(str, root_definition["entityId"]))
                occurrence_id = f"occurrence-{len(occurrences) + 1:04d}"
                occurrences.append(
                    {
                        "occurrenceId": occurrence_id,
                        "parentOccurrenceId": root_occurrence["occurrenceId"],
                        "sourceEntityId": child["entityId"],
                        "partNumber": child["partNumber"],
                        "transform": [1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0],
                    }
                )
                if child["kind"] == "assembly":
                    append_children(free_label, typing.cast(str, child["entityId"]), occurrence_id, 1)

        if len(definitions) < 2 or not any(definition["previewSource"] for definition in definitions):
            raise ProtocolFailure("artifact-invalid")
        return {
            "operation": "inspectStepAssembly",
            "sourceDigest": expected,
            "rootEntityId": root_definition["entityId"],
            "definitionCount": len(definitions),
            "occurrenceCount": len(occurrences),
            "definitions": sorted(definitions, key=lambda value: typing.cast(str, value["entityId"])),
            "occurrences": sorted(occurrences, key=lambda value: typing.cast(str, value["occurrenceId"])),
            "provenance": {"sourceDigest": expected, "sourceByteLength": len(payload)},
        }


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
        raw_source_part_id = occurrence["sourcePartId"]
        source_part_id = None if raw_source_part_id is None else _safe_identifier(raw_source_part_id)
        parent = occurrence["parentEntityId"]
        if parent is not None:
            parent = _safe_identifier(parent)
        if entity_id in occurrences or (source_part_id is not None and source_part_id not in source_requests) or parent == entity_id:
            raise ProtocolFailure("invalid-request")
        occurrences[entity_id] = {
            "entityId": entity_id,
            "sourcePartId": source_part_id,
            "parentEntityId": parent,
            "transform": _matrix(occurrence["transform"]),
        }
        if source_part_id is not None:
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
        source_id = typing.cast(str | None, occurrence["sourcePartId"])
        local = typing.cast(list[float], occurrence["transform"])
        node: dict[str, object] = {
            "matrix": _column_major(local),
            "extras": {"photonEntityId": entity_id},
        }
        if source_id is not None:
            node["mesh"] = mesh_indexes[source_id]
        if children[entity_id]:
            node["children"] = [entity_indexes[child] for child in sorted(children[entity_id])]
        nodes.append(node)
        if source_id is None:
            continue
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
                    "sourcePartId": typing.cast(str | None, occurrences[entity_id]["sourcePartId"]),
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
    if operation == "inspectStepAssembly":
        return _inspect_step_assembly(request)
    if operation == "manualSketchExtrudeAdd":
        return _manual_sketch_extrude_add(request)
    if operation == "manualSketchExtrudeCut":
        return _manual_sketch_extrude_cut(request)
    if operation == "manualHoleCut":
        return _manual_hole_cut(request)
    if operation == "manualLinearPattern":
        return _manual_pattern(request, False)
    if operation == "manualCircularPattern":
        return _manual_pattern(request, True)
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
