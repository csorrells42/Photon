import { useEffect, useState } from 'react'

export const DEFAULT_ASSISTANT_DISPLAY_NAME = 'Photon'
export const ASSISTANT_DISPLAY_NAME_STORAGE_KEY = 'photos-agape-aphthartos.assistant-display-name.v1'
const ASSISTANT_DISPLAY_NAME_CHANGED_EVENT = 'photos-assistant-display-name-changed'
const MAX_ASSISTANT_DISPLAY_NAME_LENGTH = 40
const unsafeDisplayCharacters = /[\u0000-\u001f\u007f-\u009f\u200b-\u200f\u202a-\u202e\u2060-\u206f\ufeff]/g

export function normalizeAssistantDisplayName(value: unknown): string {
  if (typeof value !== 'string') return DEFAULT_ASSISTANT_DISPLAY_NAME
  const normalized = value.normalize('NFKC').replace(unsafeDisplayCharacters, '').replace(/\s+/g, ' ').trim().slice(0, MAX_ASSISTANT_DISPLAY_NAME_LENGTH).trim()
  return normalized || DEFAULT_ASSISTANT_DISPLAY_NAME
}

export function loadAssistantDisplayName(storage: Pick<Storage, 'getItem'> | undefined = globalThis.localStorage): string {
  try { return normalizeAssistantDisplayName(storage?.getItem(ASSISTANT_DISPLAY_NAME_STORAGE_KEY)) }
  catch { return DEFAULT_ASSISTANT_DISPLAY_NAME }
}

export function saveAssistantDisplayName(value: unknown, storage: Pick<Storage, 'setItem'> | undefined = globalThis.localStorage): string {
  const normalized = normalizeAssistantDisplayName(value)
  try { storage?.setItem(ASSISTANT_DISPLAY_NAME_STORAGE_KEY, normalized) }
  catch { /* Keep the session value when storage is unavailable. */ }
  if (typeof window !== 'undefined') window.dispatchEvent(new CustomEvent(ASSISTANT_DISPLAY_NAME_CHANGED_EVENT, { detail: normalized }))
  return normalized
}

export function useAssistantDisplayName(): [string, (value: unknown) => void] {
  const [name, setName] = useState(loadAssistantDisplayName)
  useEffect(() => {
    const onChanged = (event: Event) => setName(normalizeAssistantDisplayName((event as CustomEvent<unknown>).detail))
    const onStorage = (event: StorageEvent) => { if (event.key === ASSISTANT_DISPLAY_NAME_STORAGE_KEY) setName(normalizeAssistantDisplayName(event.newValue)) }
    window.addEventListener(ASSISTANT_DISPLAY_NAME_CHANGED_EVENT, onChanged)
    window.addEventListener('storage', onStorage)
    return () => {
      window.removeEventListener(ASSISTANT_DISPLAY_NAME_CHANGED_EVENT, onChanged)
      window.removeEventListener('storage', onStorage)
    }
  }, [])
  return [name, (value: unknown) => setName(saveAssistantDisplayName(value))]
}
