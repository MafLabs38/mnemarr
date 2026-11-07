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

/// A program of Windows by its full path: a program of the same name beside the collector must never run in its place.
#[cfg(windows)]
fn system32(name: &str) -> std::path::PathBuf {
    let root = std::env::var_os("SystemRoot").unwrap_or_else(|| "C:\\Windows".into());
    std::path::Path::new(&root).join("System32").join(name)
}

/// Whether an address that a server gave is on the server the person typed (same scheme, host and port): only then is it opened without being asked.
pub fn is_same_server(server: &str, address: &str) -> bool {
    fn origin(text: &str) -> Option<(String, String)> {
        let (scheme, rest) = text.split_once("://")?;
        let authority = rest.split(['/', '?', '#']).next()?;
        (!authority.is_empty() && !authority.contains('@'))
            .then(|| (scheme.to_ascii_lowercase(), authority.to_ascii_lowercase()))
    }
    matches!((origin(server), origin(address)), (Some(a), Some(b)) if a == b)
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
        let mut command = Command::new(system32("rundll32.exe"));
        command
            .args(["url.dll,FileProtocolHandler", address])
            .creation_flags(CREATE_NO_WINDOW);
        command
    };
    #[cfg(target_os = "macos")]
    let mut command = {
        let mut command = Command::new("/usr/bin/open");
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

    #[test]
    fn should_open_an_address_only_when_it_is_on_the_server_that_was_typed() {
        let server = "https://mnemarr.example";
        assert!(is_same_server(
            server,
            "https://mnemarr.example/link?code=ABCD"
        ));
        assert!(is_same_server(
            "https://Mnemarr.Example/",
            "https://mnemarr.example/link"
        ));
        assert!(is_same_server(
            "http://10.0.0.10:8384",
            "http://10.0.0.10:8384/link?code=A"
        ));
        // Another host, another port, another scheme, or an address that hides its host behind a user name: shown, not opened.
        assert!(!is_same_server(
            server,
            "https://evil.example/link?code=ABCD"
        ));
        assert!(!is_same_server(
            "http://10.0.0.10:8384",
            "http://10.0.0.10:9999/link"
        ));
        assert!(!is_same_server(server, "http://mnemarr.example/link"));
        assert!(!is_same_server(
            server,
            "https://mnemarr.example@evil.example/link"
        ));
        assert!(!is_same_server(server, "not an address"));
    }
}
