#[cfg(windows)]
const SERVICE_NAME: &str = "EasyNetBalance";

#[cfg(windows)]
mod platform {
    use super::SERVICE_NAME;
    use std::ffi::c_void;
    use std::io;
    use std::mem::{size_of, zeroed};
    use std::os::windows::ffi::OsStrExt;
    use std::sync::atomic::{AtomicU32, Ordering};
    use std::sync::Mutex;
    use std::thread;
    use std::time::{Duration, Instant};
    use windows_service::service::{ServiceAccess, ServiceExitCode, ServiceState};
    use windows_service::service_manager::{ServiceManager, ServiceManagerAccess};

    const SERVICE_START_TIMEOUT: Duration = Duration::from_secs(30);
    const SERVICE_STOP_TIMEOUT: Duration = Duration::from_secs(45);
    const POLL_INTERVAL: Duration = Duration::from_millis(200);
    const ERROR_ACCESS_DENIED: i32 = 5;
    const ERROR_INVALID_PARAMETER: i32 = 87;
    const ERROR_SERVICE_ALREADY_RUNNING: i32 = 1056;
    const ERROR_SERVICE_DOES_NOT_EXIST: i32 = 1060;
    const ERROR_CANCELLED: i32 = 1223;
    const SYNCHRONIZE: u32 = 0x0010_0000;
    const WAIT_OBJECT_0: u32 = 0;
    const WAIT_TIMEOUT: u32 = 258;
    const WAIT_FAILED: u32 = u32::MAX;
    const TH32CS_SNAPPROCESS: u32 = 0x0000_0002;
    const ERROR_NO_MORE_FILES: i32 = 18;
    const SEE_MASK_NOCLOSEPROCESS: u32 = 0x0000_0040;

    static LAST_SERVICE_PID: AtomicU32 = AtomicU32::new(0);
    static SERVICE_PROCESS_HANDLES: Mutex<Vec<ServiceProcessHandle>> = Mutex::new(Vec::new());

    #[repr(C)]
    struct ProcessEntry32W {
        size: u32,
        usage_count: u32,
        process_id: u32,
        default_heap_id: usize,
        module_id: u32,
        thread_count: u32,
        parent_process_id: u32,
        base_priority: i32,
        flags: u32,
        executable_file: [u16; 260],
    }

    #[repr(C)]
    struct ShellExecuteInfoW {
        size: u32,
        mask: u32,
        window: *mut c_void,
        verb: *const u16,
        file: *const u16,
        parameters: *const u16,
        directory: *const u16,
        show: i32,
        instance: *mut c_void,
        id_list: *mut c_void,
        class: *const u16,
        class_key: *mut c_void,
        hot_key: u32,
        icon_or_monitor: *mut c_void,
        process: *mut c_void,
    }

    #[link(name = "kernel32")]
    extern "system" {
        fn CloseHandle(handle: *mut c_void) -> i32;
        fn OpenProcess(desired_access: u32, inherit_handle: i32, process_id: u32) -> *mut c_void;
        fn WaitForSingleObject(handle: *mut c_void, milliseconds: u32) -> u32;
        fn GetExitCodeProcess(process: *mut c_void, exit_code: *mut u32) -> i32;
        fn CreateToolhelp32Snapshot(flags: u32, process_id: u32) -> *mut c_void;
        fn Process32FirstW(snapshot: *mut c_void, entry: *mut ProcessEntry32W) -> i32;
        fn Process32NextW(snapshot: *mut c_void, entry: *mut ProcessEntry32W) -> i32;
    }

    #[link(name = "shell32")]
    extern "system" {
        fn ShellExecuteExW(info: *mut ShellExecuteInfoW) -> i32;
    }

    struct ServiceProcessHandle {
        process_id: u32,
        handle: *mut c_void,
    }

    unsafe impl Send for ServiceProcessHandle {}

    impl Drop for ServiceProcessHandle {
        fn drop(&mut self) {
            if !self.handle.is_null() {
                unsafe { CloseHandle(self.handle) };
            }
        }
    }

    fn missing_service_error() -> String {
        "EasyNetBalance service is not installed. Install EasyNetBalance to continue.".to_owned()
    }

    fn service_error(error: windows_service::Error) -> String {
        if winapi_code(&error) == Some(ERROR_SERVICE_DOES_NOT_EXIST) {
            missing_service_error()
        } else {
            format!("Could not access the EasyNetBalance service: {error}")
        }
    }

    fn winapi_code(error: &windows_service::Error) -> Option<i32> {
        match error {
            windows_service::Error::Winapi(error) => error.raw_os_error(),
            _ => None,
        }
    }

    fn open_manager() -> Result<ServiceManager, String> {
        ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT)
            .map_err(|error| format!("Could not connect to the Windows service manager: {error}"))
    }

    fn query_service(manager: &ServiceManager) -> Result<windows_service::service::Service, String> {
        manager
            .open_service(SERVICE_NAME, ServiceAccess::QUERY_STATUS)
            .map_err(service_error)
    }

    fn start_service_handle(
        manager: &ServiceManager,
    ) -> Result<windows_service::service::Service, windows_service::Error> {
        manager.open_service(
            SERVICE_NAME,
            ServiceAccess::QUERY_STATUS | ServiceAccess::START,
        )
    }

    fn remember_service_pid(process_id: Option<u32>) {
        let Some(process_id) = process_id.filter(|process_id| *process_id != 0) else {
            return;
        };
        LAST_SERVICE_PID.store(process_id, Ordering::SeqCst);

        let handle = unsafe { OpenProcess(SYNCHRONIZE, 0, process_id) };
        if handle.is_null() {
            return;
        }

        let mut handles = SERVICE_PROCESS_HANDLES
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if handles.iter().any(|existing| {
            existing.process_id == process_id
                && unsafe { WaitForSingleObject(existing.handle, 0) } != WAIT_OBJECT_0
        }) {
            unsafe { CloseHandle(handle) };
            return;
        }
        handles.retain(|existing| existing.process_id != process_id);
        handles.push(ServiceProcessHandle { process_id, handle });
    }

    fn take_remembered_process_handle(process_id: u32) -> Option<ServiceProcessHandle> {
        let mut handles = SERVICE_PROCESS_HANDLES
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        handles
            .iter()
            .position(|handle| handle.process_id == process_id)
            .map(|index| handles.remove(index))
    }

    fn remaining_milliseconds(deadline: Instant) -> u32 {
        let remaining = deadline.saturating_duration_since(Instant::now());
        remaining.as_millis().min(u32::MAX as u128) as u32
    }

    fn wait_for_process_handle(
        process: &ServiceProcessHandle,
        deadline: Instant,
    ) -> Result<(), String> {
        let result = unsafe { WaitForSingleObject(process.handle, remaining_milliseconds(deadline)) };
        match result {
            WAIT_OBJECT_0 => Ok(()),
            WAIT_TIMEOUT => Err("Timed out waiting for the EasyNetBalance service process to exit.".to_owned()),
            WAIT_FAILED => Err(format!(
                "Could not wait for the EasyNetBalance service process: {}",
                io::Error::last_os_error()
            )),
            _ => Err("Windows returned an unexpected result while waiting for the service process.".to_owned()),
        }
    }

    fn process_exists(process_id: u32) -> Result<bool, String> {
        let snapshot = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) };
        if snapshot == -1isize as *mut c_void {
            return Err(format!(
                "Could not inspect Windows processes while waiting for service shutdown: {}",
                io::Error::last_os_error()
            ));
        }
        let _snapshot = ServiceProcessHandle {
            process_id: 0,
            handle: snapshot,
        };

        let mut entry: ProcessEntry32W = unsafe { zeroed() };
        entry.size = size_of::<ProcessEntry32W>() as u32;
        let mut found = unsafe { Process32FirstW(snapshot, &mut entry) };
        if found == 0 {
            let error = io::Error::last_os_error();
            if error.raw_os_error() == Some(ERROR_NO_MORE_FILES) {
                return Ok(false);
            }
            return Err(format!("Could not read the Windows process list: {error}"));
        }

        loop {
            if entry.process_id == process_id {
                return Ok(true);
            }
            found = unsafe { Process32NextW(snapshot, &mut entry) };
            if found == 0 {
                let error = io::Error::last_os_error();
                if error.raw_os_error() == Some(ERROR_NO_MORE_FILES) {
                    return Ok(false);
                }
                return Err(format!("Could not read the Windows process list: {error}"));
            }
        }
    }

    fn wait_for_process_exit(process_id: u32, deadline: Instant) -> Result<(), String> {
        if let Some(handle) = take_remembered_process_handle(process_id) {
            return wait_for_process_handle(&handle, deadline);
        }

        let handle = unsafe { OpenProcess(SYNCHRONIZE, 0, process_id) };
        if !handle.is_null() {
            let process = ServiceProcessHandle { process_id, handle };
            return wait_for_process_handle(&process, deadline);
        }

        let open_error = io::Error::last_os_error();
        if open_error.raw_os_error() == Some(ERROR_INVALID_PARAMETER) {
            return Ok(());
        }
        if open_error.raw_os_error() != Some(ERROR_ACCESS_DENIED) {
            return Err(format!(
                "Could not open the EasyNetBalance service process: {open_error}"
            ));
        }

        while Instant::now() < deadline {
            if !process_exists(process_id)? {
                return Ok(());
            }
            thread::sleep(POLL_INTERVAL.min(deadline.saturating_duration_since(Instant::now())));
        }
        Err("Timed out waiting for the EasyNetBalance service process to exit.".to_owned())
    }

    fn status_error(error: windows_service::Error) -> String {
        if winapi_code(&error) == Some(ERROR_SERVICE_DOES_NOT_EXIST) {
            missing_service_error()
        } else {
            format!("Could not query EasyNetBalance service status: {error}")
        }
    }

    fn poll_running(
        service: &windows_service::service::Service,
        deadline: Instant,
    ) -> Result<(), String> {
        loop {
            let status = service.query_status().map_err(status_error)?;
            remember_service_pid(status.process_id);
            match status.current_state {
                ServiceState::Running => return Ok(()),
                ServiceState::Stopped => {
                    return Err("EasyNetBalance service stopped before it became ready.".to_owned())
                }
                ServiceState::Paused => {
                    return Err("EasyNetBalance service is paused. Resume it, then try again.".to_owned())
                }
                _ => {}
            }
            if Instant::now() >= deadline {
                return Err("Timed out waiting for the EasyNetBalance service to start.".to_owned());
            }
            thread::sleep(POLL_INTERVAL.min(deadline.saturating_duration_since(Instant::now())));
        }
    }

    fn start_helper_with_manager(manager: &ServiceManager) -> Result<(), String> {
        let query_service = query_service(manager)?;
        let deadline = Instant::now() + SERVICE_START_TIMEOUT;

        loop {
            let status = query_service.query_status().map_err(status_error)?;
            remember_service_pid(status.process_id);
            match status.current_state {
                ServiceState::Running => return Ok(()),
                ServiceState::Stopped => {
                    let start_service = start_service_handle(manager).map_err(|error| {
                        if winapi_code(&error) == Some(ERROR_ACCESS_DENIED) {
                            "Windows denied permission to start EasyNetBalance service. Run the service-start helper as administrator.".to_owned()
                        } else {
                            service_error(error)
                        }
                    })?;
                    match start_service.start(&[] as &[&std::ffi::OsStr]) {
                        Ok(()) => {},
                        Err(error) if winapi_code(&error) == Some(ERROR_SERVICE_ALREADY_RUNNING) => {},
                        Err(error) if winapi_code(&error) == Some(ERROR_ACCESS_DENIED) => {
                            return Err("Windows denied permission to start EasyNetBalance service. Run the service-start helper as administrator.".to_owned());
                        }
                        Err(error) => return Err(service_error(error)),
                    }
                    poll_running(&start_service, deadline)?;
                    return Ok(());
                }
                ServiceState::Paused => {
                    return Err("EasyNetBalance service is paused. Resume it, then try again.".to_owned())
                }
                _ => {
                    if Instant::now() >= deadline {
                        return Err("Timed out waiting for the EasyNetBalance service to start.".to_owned());
                    }
                    thread::sleep(POLL_INTERVAL.min(deadline.saturating_duration_since(Instant::now())));
                }
            }
        }
    }

    fn ensure_running_windows() -> Result<(), String> {
        let manager = open_manager()?;
        let service = query_service(&manager)?;
        let deadline = Instant::now() + SERVICE_START_TIMEOUT;

        loop {
            let status = service.query_status().map_err(status_error)?;
            remember_service_pid(status.process_id);
            match status.current_state {
                ServiceState::Running => return Ok(()),
                ServiceState::Stopped => {
                    let start_service = match start_service_handle(&manager) {
                        Ok(service) => service,
                        Err(error) if winapi_code(&error) == Some(ERROR_ACCESS_DENIED) => {
                            return start_elevated_helper();
                        }
                        Err(error) => return Err(service_error(error)),
                    };
                    match start_service.start(&[] as &[&std::ffi::OsStr]) {
                        Ok(()) => {},
                        Err(error) if winapi_code(&error) == Some(ERROR_SERVICE_ALREADY_RUNNING) => {},
                        Err(error) if winapi_code(&error) == Some(ERROR_ACCESS_DENIED) => {
                            return start_elevated_helper();
                        }
                        Err(error) => return Err(service_error(error)),
                    }
                    poll_running(&start_service, deadline)?;
                    return Ok(());
                }
                ServiceState::Paused => {
                    return Err("EasyNetBalance service is paused. Resume it, then try again.".to_owned())
                }
                _ => {
                    if Instant::now() >= deadline {
                        return Err("Timed out waiting for the EasyNetBalance service to start.".to_owned());
                    }
                    thread::sleep(POLL_INTERVAL.min(deadline.saturating_duration_since(Instant::now())));
                }
            }
        }
    }

    fn start_elevated_helper() -> Result<(), String> {
        let executable_path = std::env::current_exe()
            .map_err(|error| format!("Could not locate the EasyNetBalance application: {error}"))?;
        let executable: Vec<u16> = executable_path.as_os_str().encode_wide().chain(Some(0)).collect();
        let verb: Vec<u16> = "runas".encode_utf16().chain(Some(0)).collect();
        let parameters: Vec<u16> = "--start-service".encode_utf16().chain(Some(0)).collect();
        let mut info: ShellExecuteInfoW = unsafe { zeroed() };
        info.size = size_of::<ShellExecuteInfoW>() as u32;
        info.mask = SEE_MASK_NOCLOSEPROCESS;
        info.verb = verb.as_ptr();
        info.file = executable.as_ptr();
        info.parameters = parameters.as_ptr();
        info.show = 0;

        if unsafe { ShellExecuteExW(&mut info) } == 0 {
            let error = io::Error::last_os_error();
            return if error.raw_os_error() == Some(ERROR_CANCELLED) {
                Err("Administrator approval was cancelled; EasyNetBalance service could not be started.".to_owned())
            } else {
                Err(format!("Could not request administrator permission to start EasyNetBalance service: {error}"))
            };
        }
        if info.process.is_null() {
            return Err("Windows started the elevated service helper without returning a process handle.".to_owned());
        }

        // Time spent answering the UAC prompt is not service startup time.
        let deadline = Instant::now() + SERVICE_START_TIMEOUT;
        let helper = ServiceProcessHandle {
            process_id: 0,
            handle: info.process,
        };
        let wait_result = unsafe {
            WaitForSingleObject(helper.handle, remaining_milliseconds(deadline))
        };
        if wait_result == WAIT_TIMEOUT {
            return Err("Timed out waiting for the elevated EasyNetBalance service helper.".to_owned());
        }
        if wait_result == WAIT_FAILED {
            return Err(format!(
                "Could not wait for the elevated service helper: {}",
                io::Error::last_os_error()
            ));
        }
        if wait_result != WAIT_OBJECT_0 {
            return Err("Windows returned an unexpected result while waiting for the elevated service helper.".to_owned());
        }

        let mut helper_exit_code = 0;
        if unsafe { GetExitCodeProcess(helper.handle, &mut helper_exit_code) } == 0 {
            return Err(format!(
                "Could not read the elevated service helper result: {}",
                io::Error::last_os_error()
            ));
        }

        let manager = open_manager()?;
        let service = query_service(&manager)?;
        let status = service.query_status().map_err(status_error)?;
        remember_service_pid(status.process_id);
        if status.current_state != ServiceState::Running && helper_exit_code != 0 {
            return Err(format!(
                "The elevated service helper failed with exit code {helper_exit_code}."
            ));
        }
        poll_running(&service, deadline)
    }

    pub(super) fn ensure_running() -> Result<(), String> {
        ensure_running_windows()
    }

    pub(super) fn start_helper() -> Result<(), String> {
        let manager = open_manager()?;
        start_helper_with_manager(&manager)
    }

    pub(super) fn wait_stopped() -> Result<(), String> {
        let deadline = Instant::now() + SERVICE_STOP_TIMEOUT;
        let manager = open_manager()?;
        let service = match manager.open_service(SERVICE_NAME, ServiceAccess::QUERY_STATUS) {
            Ok(service) => service,
            Err(error) if winapi_code(&error) == Some(ERROR_SERVICE_DOES_NOT_EXIST) => return confirm_last_process_exit(deadline),
            Err(error) => return Err(service_error(error)),
        };

        loop {
            let status = match service.query_status() {
                Ok(status) => status,
                Err(error) if winapi_code(&error) == Some(ERROR_SERVICE_DOES_NOT_EXIST) => return confirm_last_process_exit(deadline),
                Err(error) => return Err(status_error(error)),
            };
            remember_service_pid(status.process_id);
            if status.current_state == ServiceState::Stopped {
                if !matches!(status.exit_code, ServiceExitCode::Win32(0)) {
                    return Err(format!("EasyNetBalance service reported an unsuccessful shutdown: {:?}", status.exit_code));
                }
                return confirm_last_process_exit(deadline);
            }
            if Instant::now() >= deadline {
                return Err("Timed out waiting for the EasyNetBalance service to stop.".to_owned());
            }
            thread::sleep(POLL_INTERVAL.min(deadline.saturating_duration_since(Instant::now())));
        }
    }

    fn confirm_last_process_exit(deadline: Instant) -> Result<(), String> {
        let process_id = LAST_SERVICE_PID.load(Ordering::SeqCst);
        if process_id == 0 {
            return Ok(());
        }
        wait_for_process_exit(process_id, deadline)
    }
}

pub fn ensure_running() -> Result<(), String> {
    #[cfg(windows)]
    {
        platform::ensure_running()
    }
    #[cfg(not(windows))]
    {
        Err("EasyNetBalance service lifecycle is available only on Windows.".to_owned())
    }
}

pub fn wait_stopped() -> Result<(), String> {
    #[cfg(windows)]
    {
        platform::wait_stopped()
    }
    #[cfg(not(windows))]
    {
        Ok(())
    }
}

pub fn start_helper() -> Result<(), String> {
    #[cfg(windows)]
    {
        platform::start_helper()
    }
    #[cfg(not(windows))]
    {
        Err("EasyNetBalance service mode is available only on Windows.".to_owned())
    }
}
