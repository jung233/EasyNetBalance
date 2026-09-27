import { invoke } from "@tauri-apps/api/core";

let tail: Promise<unknown> = Promise.resolve();

export function call<T>(method: string, payload?: unknown): Promise<T> {
  const request = tail.then(() => invoke<T>("ipc_call", { method, payload: payload ?? null }));
  tail = request.then(
    () => undefined,
    () => undefined,
  );
  return request;
}

export function saveExport(path: string, dataBase64: string): Promise<void> {
  return invoke<void>("write_export", { path, dataBase64 });
}
