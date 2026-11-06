//! Opens an address in the browser of the person (the page where Mnemarr asks them to approve, the page of a Mnemarr).
//!
//! The address can come from a server, so it is never handed to a shell: Windows opens it through the system's own handler, not through `cmd`, where
//! `&` or `|` in an address would be a command.

use std::process::{Command, Stdio};

/// Whether an address is one to open: `http` or `https`, with nothing in it that a person could not have typed (no space, no control character).
pub fn is_openable(address: &str) -> bool {
    (address.starts_with("https://") || address.starts_with("http://"))
        && address.len() <= 2048
        && !address.chars().any(|c| c.is_control() || c == ' ')
}

/// Opens the address in the default browser, without a window of its own. True when the browser was asked; the person may have none.
pub fn open(address: &str) -> bool {
    if !is_openable(address) {
        return false;
    }
    #[cfg(windows)]
    let mut command = {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        let mut command = Command::new("rundll32");
        command
            .args(["url.dll,FileProtocolHandler", address])
            .creation_flags(CREATE_NO_WINDOW);
        command
    };
    #[cfg(target_os = "macos")]
    let mut command = {
        let mut command = Command::new("open");
        command.arg(address);
        command
    };
    #[cfg(all(unix, not(target_os = "macos")))]
    let mut command = {
        let mut command = Command::new("xdg-open");
        command.arg(address);
        command
    };
    command
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .is_ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn should_open_only_web_addresses_that_a_shell_could_not_read_as_a_command() {
        assert!(is_openable(
            "https://mnemarr.example/link?code=ABCD-EFGH-JKLM"
        ));
        assert!(is_openable("http://127.0.0.1:8123/token/"));
        assert!(!is_openable("file:///c:/windows/system32/calc.exe"));
        assert!(!is_openable("calc.exe"));
        assert!(!is_openable("javascript:alert(1)"));
        assert!(!is_openable("https://x.example/ a"));
        assert!(!is_openable("https://x.example/\nstart calc"));
        assert!(!open("not an address"));
    }
}
