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
        Err(_) => {
            let _ = status.set_service_status(ServiceStatus {
                service_type: ServiceType::OWN_PROCESS, current_state: ServiceState::Stopped,
                controls_accepted: ServiceControlAccept::empty(),
                exit_code: ServiceExitCode::ServiceSpecific(1), checkpoint: 0,
                wait_hint: Duration::default(), process_id: None,
            });
            return;
        }
    };
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
            if let Ok(mut runtime) = scheduler_runtime.lock() { let _ = runtime.tick(); }
        }
    });
    let _ = serve_pipe(runtime, &stop, &stop_rx);
    stop.store(true, Ordering::SeqCst);
    let _ = scheduler.join();
    let _ = status.set_service_status(ServiceStatus {
        service_type: ServiceType::OWN_PROCESS, current_state: ServiceState::Stopped,
        controls_accepted: ServiceControlAccept::empty(),
        exit_code: ServiceExitCode::Win32(0), checkpoint: 0,
        wait_hint: Duration::default(), process_id: None,
    });
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
                let response = match serde_json::from_str::<serde_json::Value>(&line) {
                    Ok(request) => handle_request(&runtime, request),
                    Err(error) => serde_json::json!({"success":false,"error":format!("Invalid request: {error}"),"payload":null}),
                };
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
                let _ = file.flush();
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
