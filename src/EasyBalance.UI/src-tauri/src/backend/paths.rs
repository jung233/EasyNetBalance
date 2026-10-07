use std::path::PathBuf;

pub(super) fn application_directory() -> Result<PathBuf, String> {
    let executable = std::env::current_exe()
        .map_err(|error| format!("Could not locate the running application: {error}"))?;
    executable.parent().map(PathBuf::from)
        .ok_or_else(|| "The running application has no parent directory.".into())
}

#[cfg(windows)]
pub(super) fn common_application_data() -> Result<PathBuf, String> {
    use std::ffi::c_void;
    use std::os::windows::ffi::OsStringExt;

    #[repr(C)]
    struct Guid { data1: u32, data2: u16, data3: u16, data4: [u8; 8] }
    // FOLDERID_ProgramData: query Windows rather than assuming a drive or env var.
    const PROGRAM_DATA: Guid = Guid {
        data1: 0x62ab5d82, data2: 0xfdc1, data3: 0x4dc3,
        data4: [0xa9, 0xdd, 0x07, 0x0d, 0x1d, 0x49, 0x5d, 0x97],
    };
    #[link(name = "shell32")]
    extern "system" {
        fn SHGetKnownFolderPath(folder: *const Guid, flags: u32, token: *mut c_void, path: *mut *mut u16) -> i32;
    }
    #[link(name = "ole32")]
    extern "system" { fn CoTaskMemFree(memory: *mut c_void); }

    let mut path = std::ptr::null_mut();
    let result = unsafe { SHGetKnownFolderPath(&PROGRAM_DATA, 0, std::ptr::null_mut(), &mut path) };
    if result < 0 || path.is_null() {
        if !path.is_null() { unsafe { CoTaskMemFree(path.cast()); } }
        return Err(format!("Windows could not resolve CommonApplicationData (HRESULT {result:#x})."));
    }
    let mut length = 0;
    unsafe { while *path.add(length) != 0 { length += 1; } }
    let directory = PathBuf::from(std::ffi::OsString::from_wide(unsafe { std::slice::from_raw_parts(path, length) }));
    unsafe { CoTaskMemFree(path.cast()); }
    if directory.is_absolute() { Ok(directory) }
    else { Err("Windows returned a relative CommonApplicationData directory.".into()) }
}

#[cfg(not(windows))]
pub(super) fn common_application_data() -> Result<PathBuf, String> {
    Err("Service data storage requires Windows CommonApplicationData.".into())
}
