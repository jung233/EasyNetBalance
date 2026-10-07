use super::{handle_request, Runtime, PIPE_NAME};
use std::ffi::{c_void, OsString};
use std::io::{Read, Write};
use std::sync::{mpsc, Arc, Mutex};
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;
use std::time::{Duration, Instant};

#[cfg(windows)]
windows_service::define_windows_service!(ffi_service_main, service_main);

pub(crate) fn run_service() -> anyhow::Result<()> {
    #[cfg(windows)] {
        windows_service::service_dispatcher::start("EasyNetBalance", ffi_service_main)?;
        Ok(())
    }
    #[cfg(not(windows))] {
        anyhow::bail!("EasyNetBalance service mode is available only on Windows.")
    }
}

#[cfg(windows)]
fn service_main(_arguments: Vec<OsString>) {
    use windows_service::service::{ServiceControl, ServiceControlAccept, ServiceExitCode, ServiceState, ServiceStatus, ServiceType};
    use windows_service::service_control_handler::{self, ServiceControlHandlerResult};

    let (stop_tx, stop_rx) = mpsc::channel::<()>();
    let stop = Arc::new(AtomicBool::new(false));
    let handler_stop = stop.clone();
    let status = service_control_handler::register("EasyNetBalance", move |control| {
        match control {
            ServiceControl::Stop | ServiceControl::Shutdown => {
                handler_stop.store(true, Ordering::SeqCst);
                let _ = stop_tx.send(());
                ServiceControlHandlerResult::NoError
            }
            ServiceControl::Interrogate => ServiceControlHandlerResult::NoError,
            _ => ServiceControlHandlerResult::NotImplemented,
        }
    });
    let status = match status { Ok(status) => status, Err(_) => return };
    let start_pending = ServiceStatus {
        service_type: ServiceType::OWN_PROCESS,
        current_state: ServiceState::StartPending,
        controls_accepted: ServiceControlAccept::empty(),
        exit_code: ServiceExitCode::Win32(0), checkpoint: 1,
        wait_hint: Duration::from_secs(10), process_id: None,
    };
    if status.set_service_status(start_pending).is_err() { return; }
    let runtime = match Runtime::new() {
        Ok(runtime) => Arc::new(Mutex::new(runtime)),
        Err(error) => {
            // This path runs before Runtime's in-memory log exists. Preserve the
            // actual failure in both the protected data directory and the Windows
            // Application event log, which can be read without data-directory access.
            if let Ok(data_dir) = super::data_directory() {
                let _ = std::fs::create_dir_all(&data_dir);
                let _ = std::fs::write(data_dir.join("service-startup-error.log"), &error);
            }
            report_service_error(&format!("Service initialization failed: {error}"));
            let _ = status.set_service_status(ServiceStatus {
                service_type: ServiceType::OWN_PROCESS, current_state: ServiceState::Stopped,
                controls_accepted: ServiceControlAccept::empty(),
                exit_code: ServiceExitCode::ServiceSpecific(1), checkpoint: 0,
                wait_hint: Duration::default(), process_id: None,
            });
            return;
        }
    };
    if let Err(error) = cleanup_legacy_core() {
        if let Ok(runtime) = runtime.lock() {
            runtime.log("Warning", "Installer", &format!("Could not remove the obsolete Mihomo copy: {error}"));
        }
    }
    if status.set_service_status(ServiceStatus {
        service_type: ServiceType::OWN_PROCESS, current_state: ServiceState::Running,
        controls_accepted: ServiceControlAccept::STOP | ServiceControlAccept::SHUTDOWN,
        exit_code: ServiceExitCode::Win32(0), checkpoint: 0,
        wait_hint: Duration::default(), process_id: None,
    }).is_err() { return; }

    let scheduler_runtime = runtime.clone();
    let scheduler_stop = stop.clone();
    let scheduler = thread::spawn(move || {
        while !scheduler_stop.load(Ordering::SeqCst) {
            thread::sleep(Duration::from_secs(1));
            if scheduler_stop.load(Ordering::SeqCst) { break; }
            if let Ok(mut runtime) = scheduler_runtime.lock() { let _ = runtime.tick(&scheduler_stop); }
        }
    });
    let _ = serve_pipe(runtime.clone(), &stop, &stop_rx);
    stop.store(true, Ordering::SeqCst);
    let _ = status.set_service_status(ServiceStatus {
        service_type: ServiceType::OWN_PROCESS,
        current_state: ServiceState::StopPending,
        controls_accepted: ServiceControlAccept::empty(),
        exit_code: ServiceExitCode::Win32(0), checkpoint: 1,
        wait_hint: Duration::from_secs(15), process_id: None,
    });
    let _ = scheduler.join();
    // Runtime is still alive here, so stop its managed Mihomo child explicitly
    // before the SCM sees Stopped. Dropping std::process::Child alone does not
    // terminate a running process.
    let mut runtime = runtime.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let exit_code = match runtime.core.stop_checked() {
        Ok(()) => ServiceExitCode::Win32(0),
        Err(error) => {
            let message = format!("Service shutdown could not confirm Mihomo termination: {error}");
            runtime.log("Error", "Mihomo", &message);
            report_service_error(&message);
            ServiceExitCode::ServiceSpecific(2)
        }
    };
    let _ = status.set_service_status(ServiceStatus {
        service_type: ServiceType::OWN_PROCESS, current_state: ServiceState::Stopped,
        controls_accepted: ServiceControlAccept::empty(),
        exit_code, checkpoint: 0,
        wait_hint: Duration::default(), process_id: None,
    });
}

#[cfg(windows)]
fn report_service_error(error: &str) {
    #[link(name = "advapi32")]
    extern "system" {
        fn RegisterEventSourceW(server: *const u16, source: *const u16) -> *mut c_void;
        fn ReportEventW(handle: *mut c_void, event_type: u16, category: u16, event_id: u32,
            user_sid: *const c_void, strings: u16, data_size: u32,
            string_array: *const *const u16, raw_data: *const c_void) -> i32;
        fn DeregisterEventSource(handle: *mut c_void) -> i32;
    }
    let source: Vec<u16> = "EasyNetBalance".encode_utf16().chain(Some(0)).collect();
    let message: Vec<u16> = error.encode_utf16().chain(Some(0)).collect();
    let strings = [message.as_ptr()];
    let handle = unsafe { RegisterEventSourceW(std::ptr::null(), source.as_ptr()) };
    if !handle.is_null() {
        unsafe {
            ReportEventW(handle, 1, 0, 0x1000, std::ptr::null(), 1, 0, strings.as_ptr(), std::ptr::null());
            DeregisterEventSource(handle);
        }
    }
}

#[cfg(windows)]
fn cleanup_legacy_core() -> std::io::Result<()> {
    use std::fs;
    use std::os::windows::fs::MetadataExt;

    const REPARSE_POINT: u32 = 0x400;
    let executable = std::env::current_exe()?;
    let Some(install_root) = executable.parent() else { return Ok(()); };
    if fs::symlink_metadata(install_root)?.file_attributes() & REPARSE_POINT != 0 {
        return Err(std::io::Error::other("application directory is a reparse point"));
    }

    let manifest = install_root.join(".easynetbalance-service-files.txt");
    if manifest.exists() && fs::symlink_metadata(&manifest)?.file_attributes() & REPARSE_POINT != 0 {
        return Err(std::io::Error::other("legacy manifest is a reparse point"));
    }
    let entries = match fs::read_to_string(&manifest) {
        Ok(contents) => contents,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(()),
        Err(error) => return Err(error),
    };
    let recorded = entries.lines().map(str::trim).filter(|line| !line.is_empty()).collect::<Vec<_>>();
    if recorded.is_empty() || recorded.iter().any(|line| !line.replace('/', "\\").eq_ignore_ascii_case("core\\mihomo.exe")) {
        return Ok(());
    }

    let legacy_dir = install_root.join("core");
    let directory_metadata = match fs::symlink_metadata(&legacy_dir) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            fs::remove_file(manifest)?;
            return Ok(());
        }
        Err(error) => return Err(error),
    };
    if !directory_metadata.is_dir() || directory_metadata.file_attributes() & REPARSE_POINT != 0 {
        return Err(std::io::Error::other("legacy core is not a regular directory"));
    }
    let mut children = fs::read_dir(&legacy_dir)?;
    if let Some(child) = children.next() {
        let child = child?;
        if !child.file_name().to_string_lossy().eq_ignore_ascii_case("mihomo.exe") || children.next().is_some() {
            return Err(std::io::Error::other("legacy core contains unexpected files"));
        }
        let metadata = fs::symlink_metadata(child.path())?;
        if !metadata.is_file() || metadata.file_attributes() & REPARSE_POINT != 0 {
            return Err(std::io::Error::other("legacy Mihomo is not a regular file"));
        }
        fs::remove_file(child.path())?;
    }
    fs::remove_dir(legacy_dir)?;
    fs::remove_file(manifest)?;
    Ok(())
}

#[cfg(windows)]
fn serve_pipe(runtime: Arc<Mutex<Runtime>>, stop: &AtomicBool, stop_rx: &mpsc::Receiver<()>) -> Result<(), String> {
    use std::os::windows::io::{AsRawHandle, FromRawHandle};
    use std::ptr;

    const PIPE_ACCESS_DUPLEX: u32 = 0x00000003;
    const PIPE_TYPE_BYTE: u32 = 0;
    const PIPE_READMODE_BYTE: u32 = 0;
    const PIPE_WAIT: u32 = 0;
    const PIPE_NOWAIT: u32 = 1;
    const ERROR_PIPE_CONNECTED: u32 = 535;
    const ERROR_PIPE_LISTENING: u32 = 536;
    const ERROR_NO_DATA: u32 = 232;
    const INVALID_HANDLE_VALUE: *mut c_void = -1isize as *mut c_void;

    #[repr(C)] struct SecurityAttributes { length: u32, descriptor: *mut c_void, inherit: i32 }
    #[link(name = "advapi32")]
    extern "system" {
        fn ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl: *const u16, revision: u32, descriptor: *mut *mut c_void, size: *mut u32) -> i32;
    }
    #[link(name = "kernel32")]
    extern "system" {
        fn CreateNamedPipeW(name: *const u16, open_mode: u32, pipe_mode: u32, max_instances: u32, out_size: u32, in_size: u32, timeout: u32, security: *mut SecurityAttributes) -> *mut c_void;
        fn ConnectNamedPipe(pipe: *mut c_void, overlapped: *mut c_void) -> i32;
        fn DisconnectNamedPipe(pipe: *mut c_void) -> i32;
        fn SetNamedPipeHandleState(pipe: *mut c_void, mode: *const u32, max_collection: *const u32, timeout: *const u32) -> i32;
        fn PeekNamedPipe(pipe: *mut c_void, buffer: *mut c_void, size: u32, read: *mut u32, available: *mut u32, left: *mut u32) -> i32;
        fn ReadFile(file: *mut c_void, buffer: *mut c_void, count: u32, read: *mut u32, overlapped: *mut c_void) -> i32;
        fn WriteFile(file: *mut c_void, buffer: *const c_void, count: u32, written: *mut u32, overlapped: *mut c_void) -> i32;
        fn LocalFree(memory: *mut c_void) -> *mut c_void;
    }

    let name: Vec<u16> = format!(r"\\.\pipe\{PIPE_NAME}").encode_utf16().chain(Some(0)).collect();
    let sddl: Vec<u16> = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;AU)(D;;GA;;;NU)".encode_utf16().chain(Some(0)).collect();
    while !stop.load(Ordering::SeqCst) {
        let mut descriptor = ptr::null_mut();
        if unsafe { ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.as_ptr(), 1, &mut descriptor, ptr::null_mut()) } == 0 {
            return Err(format!("Could not build named pipe ACL: {}", std::io::Error::last_os_error()));
        }
        let mut attributes = SecurityAttributes { length: std::mem::size_of::<SecurityAttributes>() as u32, descriptor, inherit: 0 };
        let handle = unsafe { CreateNamedPipeW(name.as_ptr(), PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_NOWAIT,
            1, 64 * 1024, 64 * 1024, 0, &mut attributes) };
        unsafe { LocalFree(descriptor); }
        if handle == INVALID_HANDLE_VALUE { return Err(format!("Could not create control pipe: {}", std::io::Error::last_os_error())); }
        loop {
            if stop.load(Ordering::SeqCst) { unsafe { DisconnectNamedPipe(handle); drop(std::fs::File::from_raw_handle(handle)); } return Ok(()); }
            let connected = unsafe { ConnectNamedPipe(handle, ptr::null_mut()) } != 0;
            if connected || std::io::Error::last_os_error().raw_os_error() == Some(ERROR_PIPE_CONNECTED as i32) { break; }
            if std::io::Error::last_os_error().raw_os_error() != Some(ERROR_PIPE_LISTENING as i32) { break; }
            thread::sleep(Duration::from_millis(100));
        }
        let wait_mode = PIPE_WAIT;
        let _ = unsafe { SetNamedPipeHandleState(handle, &wait_mode, ptr::null(), ptr::null()) };
        let mut file = unsafe { std::fs::File::from_raw_handle(handle) };
        match read_line(&mut file, stop) {
            Ok(Some(line)) => {
                let (response, shutdown_request) = match serde_json::from_str::<serde_json::Value>(&line) {
                    Ok(request) => {
                        let shutdown = request.get("method").and_then(serde_json::Value::as_str) == Some("Shutdown");
                        (handle_request(&runtime, request), shutdown)
                    }
                    Err(error) => (serde_json::json!({"success":false,"error":format!("Invalid request: {error}"),"payload":null}), false),
                };
                let shutdown_succeeded = shutdown_request
                    && response.get("success").and_then(serde_json::Value::as_bool) == Some(true);
                let mut bytes = serde_json::to_vec(&response).map_err(|e| e.to_string())?;
                bytes.push(b'\n');
                if bytes.len() > 64 * 1024 * 1024 { bytes = b"{\"success\":false,\"error\":\"Response exceeds 64 MiB.\",\"payload\":null}\n".to_vec(); }
                let mut offset = 0usize;
                while offset < bytes.len() {
                    let mut written = 0u32;
                    let okay = unsafe { WriteFile(file.as_raw_handle(), bytes[offset..].as_ptr() as *const c_void, (bytes.len() - offset) as u32, &mut written, ptr::null_mut()) };
                    if okay == 0 || written == 0 { break; }
                    offset += written as usize;
                }
                let response_written = offset == bytes.len();
                // File::flush is a no-op for an unbuffered File. A named pipe
                // must drain before DisconnectNamedPipe discards unread bytes.
                let response_drained = match drain_response(&file, stop) {
                    Ok(()) => true,
                    Err(error) => {
                        if let Ok(runtime) = runtime.lock() {
                            runtime.log("Warning", "Control", &format!("Could not deliver the complete control response: {error}"));
                        }
                        false
                    }
                };
                if shutdown_succeeded && response_written && response_drained {
                    // The UI has received the successful response. Returning
                    // lets service_main stop its scheduler before SCM Stopped.
                    stop.store(true, Ordering::SeqCst);
                }
            }
            Ok(None) => {},
            Err(error) if error.raw_os_error() == Some(ERROR_NO_DATA as i32) => {},
            Err(_) => {},
        }
        unsafe { DisconnectNamedPipe(file.as_raw_handle()); }
        drop(file);
        let _ = stop_rx.try_recv();
    }
    Ok(())
}

#[cfg(windows)]
fn drain_response(file: &std::fs::File, stop: &AtomicBool) -> std::io::Result<()> {
    use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
    use std::ptr;
    #[link(name = "kernel32")]
    extern "system" {
        fn FlushFileBuffers(handle: *mut c_void) -> i32;
        fn GetCurrentProcess() -> *mut c_void;
        fn GetCurrentThread() -> *mut c_void;
        fn DuplicateHandle(source_process: *mut c_void, source: *mut c_void, target_process: *mut c_void,
            target: *mut *mut c_void, access: u32, inherit: i32, options: u32) -> i32;
        fn CancelSynchronousIo(thread: *mut c_void) -> i32;
    }
    let mut handle = ptr::null_mut();
    let duplicated = unsafe {
        DuplicateHandle(GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(), &mut handle, 0, 0, 2)
    };
    if duplicated == 0 { return Err(std::io::Error::last_os_error()); }
    let serving_thread = unsafe { OwnedHandle::from_raw_handle(handle) };
    let (done_tx, done_rx) = mpsc::channel::<()>();
    thread::scope(|scope| {
        scope.spawn(move || {
            let deadline = Instant::now() + Duration::from_secs(10);
            loop {
                match done_rx.recv_timeout(Duration::from_millis(50)) {
                    Ok(()) | Err(mpsc::RecvTimeoutError::Disconnected) => return,
                    Err(mpsc::RecvTimeoutError::Timeout) => {},
                }
                // A client that stops reading must not prevent service shutdown.
                if stop.load(Ordering::SeqCst) || Instant::now() >= deadline {
                    unsafe { CancelSynchronousIo(serving_thread.as_raw_handle()); }
                }
            }
        });
        let okay = unsafe { FlushFileBuffers(file.as_raw_handle()) };
        let result = if okay != 0 { Ok(()) } else { Err(std::io::Error::last_os_error()) };
        let _ = done_tx.send(());
        result
    })
}

#[cfg(windows)]
fn read_line(file: &mut std::fs::File, stop: &AtomicBool) -> std::io::Result<Option<String>> {
    use std::os::windows::io::AsRawHandle;
    use std::ptr;
    const ERROR_MORE_DATA: i32 = 234;
    #[link(name = "kernel32")]
    extern "system" { fn PeekNamedPipe(pipe: *mut c_void, buffer: *mut c_void, size: u32, read: *mut u32, available: *mut u32, left: *mut u32) -> i32; fn ReadFile(file: *mut c_void, buffer: *mut c_void, count: u32, read: *mut u32, overlapped: *mut c_void) -> i32; }
    let start = Instant::now(); let mut bytes = Vec::new();
    loop {
        if stop.load(Ordering::SeqCst) { return Ok(None); }
        let mut available = 0u32;
        let okay = unsafe { PeekNamedPipe(file.as_raw_handle(), ptr::null_mut(), 0, ptr::null_mut(), &mut available, ptr::null_mut()) };
        if okay == 0 { return Err(std::io::Error::last_os_error()); }
        if available > 0 {
            let mut chunk = [0u8; 4096]; let mut read = 0u32;
            let length = available.min(chunk.len() as u32);
            let okay = unsafe { ReadFile(file.as_raw_handle(), chunk.as_mut_ptr() as *mut c_void, length, &mut read, ptr::null_mut()) };
            if okay == 0 && std::io::Error::last_os_error().raw_os_error() != Some(ERROR_MORE_DATA) { return Err(std::io::Error::last_os_error()); }
            for byte in &chunk[..read as usize] {
                if *byte == b'\n' { return String::from_utf8(bytes).map(Some).map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e)); }
                bytes.push(*byte);
                if bytes.len() > 1024 * 1024 { return Err(std::io::Error::new(std::io::ErrorKind::InvalidData, "Request exceeds 1 MiB.")); }
            }
        } else { thread::sleep(Duration::from_millis(25)); }
        if start.elapsed() >= Duration::from_secs(10) { return Ok(None); }
    }
}
