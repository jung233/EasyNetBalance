#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use serde::Deserialize;
use serde_json::{json, Value};
use std::path::PathBuf;
use tauri::State;

mod backend;

#[derive(Clone)]
struct IpcGate(std::sync::Arc<std::sync::Mutex<()>>);

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct PipeResponse {
    success: bool,
    error: Option<String>,
    payload: Option<Value>,
}

#[tauri::command]
async fn ipc_call(
    method: String,
    payload: Option<Value>,
    gate: State<'_, IpcGate>,
) -> Result<Value, String> {
    let gate = gate.inner().0.clone();
    tauri::async_runtime::spawn_blocking(move || {
        let _guard = gate
            .lock()
            .map_err(|_| "The service bridge is unavailable.".to_owned())?;
        call_service(&method, payload.unwrap_or(Value::Null))
    })
    .await
    .map_err(|error| format!("The service bridge task failed: {error}"))?
}

#[tauri::command]
fn write_export(path: String, data_base64: String) -> Result<(), String> {
    let destination = PathBuf::from(path);
    if destination.file_name().is_none() {
        return Err("Choose a valid destination file.".to_owned());
    }
    let bytes = base64::Engine::decode(&base64::engine::general_purpose::STANDARD, data_base64)
        .map_err(|_| "The service returned an invalid diagnostics archive.".to_owned())?;
    std::fs::write(&destination, bytes)
        .map_err(|error| format!("Could not save diagnostics: {error}"))
}

#[cfg(windows)]
fn call_service(method: &str, payload: Value) -> Result<Value, String> {
    use std::sync::mpsc;
    use std::time::Duration;

    const THREAD_TERMINATE: u32 = 0x0001;

    #[link(name = "kernel32")]
    extern "system" {
        fn GetCurrentThreadId() -> u32;
        fn OpenThread(desired_access: u32, inherit_handle: i32, thread_id: u32) -> *mut std::ffi::c_void;
        fn CancelSynchronousIo(thread: *mut std::ffi::c_void) -> i32;
        fn CloseHandle(handle: *mut std::ffi::c_void) -> i32;
    }

    let is_telemetry = method == "GetConnectionTelemetry";
    let method = method.to_owned();
    let timeout = if is_telemetry {
        Duration::from_secs(8)
    } else {
        Duration::from_secs(45)
    };
    let (thread_sender, thread_receiver) = mpsc::sync_channel(1);
    let (result_sender, result_receiver) = mpsc::sync_channel(1);
    let _worker = std::thread::spawn(move || {
        let _ = thread_sender.send(unsafe { GetCurrentThreadId() });
        let result = call_service_sync(&method, payload);
        let _ = result_sender.send(result);
    });
    let worker_id = thread_receiver
        .recv()
        .map_err(|_| "The service bridge worker could not start.".to_owned())?;

    match result_receiver.recv_timeout(timeout) {
        Ok(result) => result,
        Err(mpsc::RecvTimeoutError::Timeout) => {
            let worker = unsafe { OpenThread(THREAD_TERMINATE, 0, worker_id) };
            if !worker.is_null() {
                unsafe {
                    CancelSynchronousIo(worker);
                    CloseHandle(worker);
                }
            }
            Err(if is_telemetry {
                "The service did not return telemetry in time. Check that the service is responsive.".to_owned()
            } else {
                "The service did not respond within 45 seconds. Check that EasyNetBalance is running.".to_owned()
            })
        }
        Err(mpsc::RecvTimeoutError::Disconnected) => {
            Err("The service bridge worker stopped unexpectedly.".to_owned())
        }
    }
}

#[cfg(windows)]
fn call_service_sync(method: &str, payload: Value) -> Result<Value, String> {
    use std::ffi::c_void;
    use std::io::{BufRead, BufReader, Read, Write};
    use std::os::windows::io::FromRawHandle;

    const GENERIC_READ: u32 = 0x8000_0000;
    const GENERIC_WRITE: u32 = 0x4000_0000;
    const OPEN_EXISTING: u32 = 3;
    const FILE_ATTRIBUTE_NORMAL: u32 = 0x80;
    const ERROR_PIPE_BUSY: i32 = 231;
    const INVALID_HANDLE_VALUE: *mut c_void = -1isize as *mut c_void;

    #[link(name = "kernel32")]
    extern "system" {
        fn CreateFileW(
            file_name: *const u16,
            desired_access: u32,
            share_mode: u32,
            security_attributes: *mut c_void,
            creation_disposition: u32,
            flags_and_attributes: u32,
            template_file: *mut c_void,
        ) -> *mut c_void;
        fn WaitNamedPipeW(pipe_name: *const u16, timeout: u32) -> i32;
    }

    fn connect(pipe_name: &[u16]) -> Result<std::fs::File, String> {
        let mut handle = unsafe {
            CreateFileW(
                pipe_name.as_ptr(),
                GENERIC_READ | GENERIC_WRITE,
                0,
                std::ptr::null_mut(),
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                std::ptr::null_mut(),
            )
        };
        if handle == INVALID_HANDLE_VALUE {
            let first_error = std::io::Error::last_os_error();
            if first_error.raw_os_error() == Some(ERROR_PIPE_BUSY) {
                let available = unsafe { WaitNamedPipeW(pipe_name.as_ptr(), 5000) };
                if available != 0 {
                    handle = unsafe {
                        CreateFileW(
                            pipe_name.as_ptr(),
                            GENERIC_READ | GENERIC_WRITE,
                            0,
                            std::ptr::null_mut(),
                            OPEN_EXISTING,
                            FILE_ATTRIBUTE_NORMAL,
                            std::ptr::null_mut(),
                        )
                    };
                }
            }
        }
        if handle == INVALID_HANDLE_VALUE {
            let error = std::io::Error::last_os_error();
            return Err(match error.raw_os_error() {
                Some(2) | Some(53) => {
                    "EasyNetBalance service is unavailable. Install or start the service, then refresh.".to_owned()
                }
                Some(ERROR_PIPE_BUSY) => {
                    "EasyNetBalance service is busy. Wait a moment and try again.".to_owned()
                }
                _ => format!("Could not connect to the EasyNetBalance service: {error}"),
            });
        }
        Ok(unsafe { std::fs::File::from_raw_handle(handle) })
    }

    let mut pipe_name: Vec<u16> = r"\\.\pipe\EasyBalance.Control.v1"
        .encode_utf16()
        .collect();
    pipe_name.push(0);
    let mut pipe = connect(&pipe_name)?;
    let request = json!({ "method": method, "payload": payload });
    let mut request_line = serde_json::to_vec(&request)
        .map_err(|error| format!("Could not encode the service request: {error}"))?;
    request_line.push(b'\n');
    if request_line.len() > 1024 * 1024 {
        return Err("The service request exceeded the 1 MiB limit.".to_owned());
    }
    pipe.write_all(&request_line)
        .and_then(|_| pipe.flush())
        .map_err(|error| format!("Could not send a request to the service: {error}"))?;

    let mut reader = BufReader::new(pipe).take(64 * 1024 * 1024 + 1);
    let mut response_line = String::new();
    reader
        .read_line(&mut response_line)
        .map_err(|error| format!("Could not read the service response: {error}"))?;
    if response_line.is_empty() {
        return Err("The service returned an empty response.".to_owned());
    }
    if response_line.len() > 64 * 1024 * 1024 {
        return Err("The service response exceeded the 64 MiB limit.".to_owned());
    }
    let response: PipeResponse = serde_json::from_str(&response_line)
        .map_err(|error| format!("The service returned an unreadable response: {error}"))?;
    if !response.success {
        return Err(response
            .error
            .unwrap_or_else(|| "The service could not complete the request.".to_owned()));
    }
    Ok(response.payload.unwrap_or(Value::Null))
}

#[cfg(not(windows))]
fn call_service(_method: &str, _payload: Value) -> Result<Value, String> {
    Err("EasyNetBalance currently requires Windows to connect to its service.".to_owned())
}

fn main() {
    if std::env::args().skip(1).any(|argument| argument == "--service") {
        if let Err(error) = backend::run_service() {
            eprintln!("EasyNetBalance service failed: {error}");
            std::process::exit(1);
        }
        return;
    }
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .manage(IpcGate(std::sync::Arc::new(std::sync::Mutex::new(()))))
        .invoke_handler(tauri::generate_handler![ipc_call, write_export])
        .run(tauri::generate_context!())
        .expect("error while running EasyNetBalance");
}
