//! Joins this machine to Mnemarr (ADR 0027 §11): exchanges the one-time code a person approved for the key of this machine, and keeps the key
//! and the address in the folder of the collector, where only this user reads them.

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use serde_json::{Value, json};

use crate::config::{BOOTSTRAP_FILE, KEY_FILE};
use crate::upload::{UploadError, agent, reason_of};

/// What the server gave back: the key of this machine, shown once, the name it is bound to, and its kind.
#[derive(Debug, PartialEq, Eq)]
pub struct Enrolled {
    pub key: String,
    pub machine: String,
    pub kind: String,
}

/// The exchange of a code: anonymous, the code is all that is needed. The answer to a wrong code says nothing of what was wrong.
pub fn exchange(server: &str, code: &str, machine: &str) -> Result<Enrolled, UploadError> {
    let mut response = agent()
        .post(format!(
            "{}/api/v1/ingest/enrol",
            server.trim_end_matches('/')
        ))
        // A write to the API needs this header unless it carries a key: a browser cannot add it to a form.
        .header("X-Mnemarr-Request", "1")
        .send_json(json!({ "code": code, "machine": machine }))
        .map_err(|error| UploadError::Later(error.to_string()))?;
    let status = response.status().as_u16();
    let body = response.body_mut().read_to_string().unwrap_or_default();
    match status {
        200 => parse(&body),
        429 => Err(UploadError::Later(
            "too many wrong codes: try again in a minute".to_string(),
        )),
        408 | 425 | 500..=599 => Err(UploadError::Later(format!("status {status}"))),
        _ => Err(UploadError::Refused(status, reason_of(&body))),
    }
}

fn parse(body: &str) -> Result<Enrolled, UploadError> {
    let value: Value = serde_json::from_str(body)
        .map_err(|_| UploadError::Later("the answer is not the one expected".to_string()))?;
    let field = |name: &str| value.get(name).and_then(Value::as_str).map(str::to_string);
    match (field("key"), field("machine"), field("kind")) {
        (Some(key), Some(machine), Some(kind)) if !key.is_empty() => {
            Ok(Enrolled { key, machine, kind })
        }
        _ => Err(UploadError::Later(
            "the answer is not the one expected".to_string(),
        )),
    }
}

/// Writes the key, then the small file that says where Mnemarr is and what this machine is called. Returns the two paths.
pub fn save(
    dir: &Path,
    server: &str,
    enrolled: &Enrolled,
    insecure: bool,
) -> io::Result<(PathBuf, PathBuf)> {
    fs::create_dir_all(dir)?;
    let key = dir.join(KEY_FILE);
    write_private(&key, &enrolled.key)?;
    let bootstrap = dir.join(BOOTSTRAP_FILE);
    let text = serde_json::to_string_pretty(
        &json!({ "server": server.trim_end_matches('/'), "machine": enrolled.machine, "insecure": insecure }),
    )
    .map_err(io::Error::other)?;
    fs::write(&bootstrap, text)?;
    Ok((key, bootstrap))
}

/// A file only its user can read: created with that mode from the start, so that the key is never readable by others, even for a moment.
#[cfg(unix)]
fn write_private(path: &Path, text: &str) -> io::Result<()> {
    use std::io::Write;
    use std::os::unix::fs::{OpenOptionsExt, PermissionsExt};
    // An older file is replaced, not written into: its mode and its owner are not trusted.
    match fs::remove_file(path) {
        Err(error) if error.kind() != io::ErrorKind::NotFound => return Err(error),
        _ => {}
    }
    let mut file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .mode(0o600)
        .open(path)?;
    file.write_all(text.as_bytes())?;
    fs::set_permissions(path, fs::Permissions::from_mode(0o600))
}

/// On Windows the folder is in the profile of the user, whose access is inherited from the profile; the access of everyone else is taken away
/// as well, as far as the system lets this user do it.
#[cfg(not(unix))]
fn write_private(path: &Path, text: &str) -> io::Result<()> {
    // The file is made empty and closed down first, then the text is written: the key is never in a file that others can read.
    fs::write(path, "")?;
    match std::env::var_os("USERNAME") {
        Some(user) => {
            // The program of the system, by its full path: a file of that name in the folder this program was started from is not run.
            let system = std::env::var_os("SystemRoot").unwrap_or_else(|| "C:\\Windows".into());
            let restricted =
                std::process::Command::new(Path::new(&system).join("System32").join("icacls.exe"))
                    .arg(path)
                    .args(["/inheritance:r", "/grant:r"])
                    .arg(format!("{}:F", user.to_string_lossy()))
                    .output()
                    .is_ok_and(|output| output.status.success());
            if !restricted {
                eprintln!(
                    "warning: the access to {} could not be limited to this user: it is the access of the folder",
                    path.display()
                );
            }
        }
        None => eprintln!(
            "warning: the access to {} could not be limited to this user (USERNAME is not set)",
            path.display()
        ),
    }
    fs::write(path, text)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{Read, Write};
    use std::net::TcpListener;

    fn serve_once(status: &str, body: &str) -> (String, std::thread::JoinHandle<String>) {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let address = format!("http://{}", listener.local_addr().unwrap());
        let answer = format!(
            "HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
            body.len()
        );
        let handle = std::thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            let mut request = Vec::new();
            let mut chunk = [0u8; 4096];
            loop {
                let read = stream.read(&mut chunk).unwrap();
                request.extend_from_slice(&chunk[..read]);
                let text = String::from_utf8_lossy(&request).to_string();
                if text.contains("\"machine\"") || read == 0 {
                    break;
                }
            }
            stream.write_all(answer.as_bytes()).unwrap();
            String::from_utf8_lossy(&request).to_string()
        });
        (address, handle)
    }

    fn folder(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("mnemarr-enrol-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        dir
    }

    #[test]
    fn should_exchange_the_code_for_a_key_and_send_it_with_the_header_a_write_needs() {
        let (address, handle) = serve_once(
            "200 OK",
            r#"{"key":"mnm_the-key","machine":"pc","kind":"intermittent"}"#,
        );

        let enrolled = exchange(&address, "ABCD-EFGH-JKLM", "pc").unwrap();

        assert_eq!(
            enrolled,
            Enrolled {
                key: "mnm_the-key".to_string(),
                machine: "pc".to_string(),
                kind: "intermittent".to_string()
            }
        );
        let request = handle.join().unwrap().to_ascii_lowercase();
        assert!(request.contains("post /api/v1/ingest/enrol"));
        assert!(request.contains("x-mnemarr-request: 1"));
        assert!(request.contains("abcd-efgh-jklm"));
        assert!(
            !request.contains("x-api-key"),
            "no key yet: the code is all there is"
        );
    }

    #[test]
    fn should_say_what_the_server_said_when_the_code_is_refused_and_try_again_later_when_it_is_busy()
     {
        let (address, handle) = serve_once(
            "400 Bad Request",
            r#"{"title":"The code is not valid: it may be wrong, used, expired or not approved yet."}"#,
        );
        let refused = exchange(&address, "ABCD-EFGH-JKLM", "pc").unwrap_err();
        handle.join().unwrap();
        assert_eq!(
            refused,
            UploadError::Refused(
                400,
                "The code is not valid: it may be wrong, used, expired or not approved yet."
                    .to_string()
            )
        );

        let (address, handle) = serve_once("429 Too Many Requests", "{}");
        assert!(matches!(
            exchange(&address, "ABCD-EFGH-JKLM", "pc"),
            Err(UploadError::Later(_))
        ));
        handle.join().unwrap();
    }

    #[test]
    fn should_not_take_an_answer_without_a_key() {
        let (address, handle) = serve_once("200 OK", r#"{"machine":"pc","kind":"intermittent"}"#);

        assert!(matches!(
            exchange(&address, "ABCD-EFGH-JKLM", "pc"),
            Err(UploadError::Later(_))
        ));
        handle.join().unwrap();
    }

    #[test]
    fn should_keep_the_key_and_the_address_where_only_this_user_reads_the_key() {
        let dir = folder("save");
        let enrolled = Enrolled {
            key: "mnm_the-key".to_string(),
            machine: "pc".to_string(),
            kind: "intermittent".to_string(),
        };

        let (key, bootstrap) = save(&dir, "https://mnemarr.example/", &enrolled, false).unwrap();

        assert_eq!(fs::read_to_string(&key).unwrap(), "mnm_the-key");
        let said: Value = serde_json::from_str(&fs::read_to_string(&bootstrap).unwrap()).unwrap();
        assert_eq!(said["server"], "https://mnemarr.example");
        assert_eq!(said["machine"], "pc");
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            assert_eq!(
                fs::metadata(&key).unwrap().permissions().mode() & 0o777,
                0o600
            );
        }
        // Enrolling again replaces the key, whatever mode the old file had.
        save(
            &dir,
            "https://mnemarr.example",
            &Enrolled {
                key: "mnm_other".to_string(),
                ..enrolled
            },
            false,
        )
        .unwrap();
        assert_eq!(fs::read_to_string(&key).unwrap(), "mnm_other");
        let _ = fs::remove_dir_all(&dir);
    }
}
