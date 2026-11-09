//! Joins this machine to Mnemarr: exchanges the one-time code a person approved for the key of this machine (ADR 0027 §11), or asks to be linked and waits for
//! a person to approve in Mnemarr (ADR 0030). The key is kept by `destinations`, in the folder of the collector, where only this user reads it.

use std::fs;
use std::io;
use std::path::Path;
use std::time::Duration;

use serde_json::{Value, json};

use crate::upload::{UploadError, agent, reason_of};

/// What the server gave back: the key of this machine, shown once, the name it is bound to, and its kind.
#[derive(Debug, PartialEq, Eq)]
pub struct Enrolled {
    pub key: String,
    pub machine: String,
    pub kind: String,
}

/// Adds the identifier of this computer to what is sent to join a Mnemarr, when the system has one (ADR 0031): a Mnemarr that knows the computer keeps
/// it as the machine it was, under whatever name.
fn with_computer(mut body: Value) -> Value {
    if let (Some(id), Some(map)) = (crate::computer::id(), body.as_object_mut()) {
        map.insert("computerId".to_string(), Value::String(id.to_string()));
    }
    body
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
        .send_json(with_computer(json!({ "code": code, "machine": machine })))
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

/// What Mnemarr gave a machine that asked to be linked: the short code to show, the secret to ask with, the address a person opens, and the timing.
#[derive(Debug, PartialEq, Eq, Clone)]
pub struct PairingRequest {
    pub user_code: String,
    pub device_code: String,
    pub verification_url: String,
    pub expires_in: Duration,
    pub interval: Duration,
}

/// What became of a request to be linked, as the machine learns it by asking again.
#[derive(Debug, PartialEq, Eq)]
pub enum PairAnswer {
    Pending,
    Approved(Enrolled),
    Rejected,
    Expired,
}

fn post_json(server: &str, path: &str, body: Value) -> Result<(u16, String), UploadError> {
    let mut response = agent()
        .post(format!("{}{path}", server.trim_end_matches('/')))
        // A write to the API needs this header unless it carries a key: a browser cannot add it to a form.
        .header("X-Mnemarr-Request", "1")
        .send_json(body)
        .map_err(|error| UploadError::Later(error.to_string()))?;
    let status = response.status().as_u16();
    let text = response.body_mut().read_to_string().unwrap_or_default();
    Ok((status, text))
}

fn unexpected() -> UploadError {
    UploadError::Later("the answer is not the one expected".to_string())
}

/// Asks Mnemarr to link this machine: it answers with a short code and an address, and a person who manages the collectors approves there.
pub fn pair_start(server: &str, hostname: &str) -> Result<PairingRequest, UploadError> {
    let (status, body) = post_json(
        server,
        "/api/v1/ingest/pair",
        with_computer(json!({ "hostname": hostname })),
    )?;
    match status {
        200 | 201 => {
            let value: Value = serde_json::from_str(&body).map_err(|_| unexpected())?;
            let text = |name: &str| value.get(name).and_then(Value::as_str).map(str::to_string);
            let seconds = |name: &str| value.get(name).and_then(Value::as_u64);
            match (
                text("userCode"),
                text("deviceCode"),
                text("verificationUrl"),
                seconds("expiresInSeconds"),
                seconds("intervalSeconds"),
            ) {
                (
                    Some(user_code),
                    Some(device_code),
                    Some(verification_url),
                    Some(life),
                    Some(every),
                ) => Ok(PairingRequest {
                    user_code,
                    device_code,
                    verification_url,
                    // A server decides how long it waits, but not for ever and not in a way that overflows: a request lives an hour at most, and is asked about every minute at most.
                    expires_in: Duration::from_secs(life.min(3600)),
                    interval: Duration::from_secs(every.clamp(1, 60)),
                }),
                _ => Err(unexpected()),
            }
        }
        429 | 408 | 425 | 500..=599 => Err(UploadError::Later(format!("status {status}"))),
        _ => Err(UploadError::Refused(status, reason_of(&body))),
    }
}

/// Asks what became of the request. The key is in the answer once, to the first to ask after the approval.
pub fn pair_poll(server: &str, device_code: &str) -> Result<PairAnswer, UploadError> {
    let (status, body) = post_json(
        server,
        "/api/v1/ingest/pair/poll",
        json!({ "deviceCode": device_code }),
    )?;
    match status {
        200 => {
            let value: Value = serde_json::from_str(&body).map_err(|_| unexpected())?;
            match value.get("state").and_then(Value::as_str) {
                Some("pending") => Ok(PairAnswer::Pending),
                Some("rejected") => Ok(PairAnswer::Rejected),
                Some("expired") => Ok(PairAnswer::Expired),
                Some("approved") => parse(&body).map(PairAnswer::Approved),
                _ => Err(unexpected()),
            }
        }
        429 | 408 | 425 | 500..=599 => Err(UploadError::Later(format!("status {status}"))),
        _ => Err(UploadError::Refused(status, reason_of(&body))),
    }
}

/// How a request to be linked ended.
#[derive(Debug, PartialEq, Eq)]
pub enum LinkOutcome {
    Linked(Enrolled),
    Rejected,
    Expired,
}

/// Asks to be linked and waits for a person. `show` is told the request as soon as it exists (to print the address and open the browser), and `wait` is
/// how the time passes (a test does not sleep). A server that cannot be reached for a moment is asked again until the request would have expired anyway.
pub fn link_by_asking(
    server: &str,
    hostname: &str,
    show: &dyn Fn(&PairingRequest),
    wait: &dyn Fn(Duration),
) -> Result<LinkOutcome, UploadError> {
    let request = pair_start(server, hostname)?;
    show(&request);
    let mut waited = Duration::ZERO;
    while waited <= request.expires_in + request.interval {
        wait(request.interval);
        waited += request.interval;
        match pair_poll(server, &request.device_code) {
            Ok(PairAnswer::Pending) | Err(UploadError::Later(_)) => {}
            Ok(PairAnswer::Approved(enrolled)) => return Ok(LinkOutcome::Linked(enrolled)),
            Ok(PairAnswer::Rejected) => return Ok(LinkOutcome::Rejected),
            Ok(PairAnswer::Expired) => return Ok(LinkOutcome::Expired),
            Err(error) => return Err(error),
        }
    }
    Ok(LinkOutcome::Expired)
}

/// A file only its user can read: created with that mode from the start, so that the key is never readable by others, even for a moment.
#[cfg(unix)]
pub(crate) fn write_private(path: &Path, text: &str) -> io::Result<()> {
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
pub(crate) fn write_private(path: &Path, text: &str) -> io::Result<()> {
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

    /// A server that answers the requests it gets in turn with the answers it was given, and keeps what it was asked.
    fn serve_in_turn(
        answers: Vec<(&'static str, String)>,
    ) -> (String, std::thread::JoinHandle<Vec<String>>) {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let address = format!("http://{}", listener.local_addr().unwrap());
        let handle = std::thread::spawn(move || {
            let mut asked = Vec::new();
            for (status, body) in answers {
                let (mut stream, _) = listener.accept().unwrap();
                let mut request = Vec::new();
                let mut chunk = [0u8; 4096];
                loop {
                    let read = stream.read(&mut chunk).unwrap();
                    request.extend_from_slice(&chunk[..read]);
                    let text = String::from_utf8_lossy(&request).to_string();
                    if let Some((head, rest)) = text.split_once("\r\n\r\n") {
                        let length = head
                            .to_ascii_lowercase()
                            .lines()
                            .find_map(|line| {
                                line.strip_prefix("content-length: ")
                                    .and_then(|v| v.trim().parse::<usize>().ok())
                            })
                            .unwrap_or(0);
                        if rest.len() >= length {
                            break;
                        }
                    }
                    if read == 0 {
                        break;
                    }
                }
                asked.push(String::from_utf8_lossy(&request).to_string());
                let answer = format!(
                    "HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                    body.len()
                );
                stream.write_all(answer.as_bytes()).unwrap();
            }
            asked
        });
        (address, handle)
    }

    const STARTED: &str = r#"{"userCode":"ABCD-EFGH-JKLM","deviceCode":"dvc_secret","verificationUrl":"https://m.example/link?code=ABCD-EFGH-JKLM","expiresInSeconds":600,"intervalSeconds":3}"#;

    #[test]
    fn should_ask_to_be_linked_show_the_address_wait_and_take_the_key_once_a_person_approved() {
        let (address, handle) = serve_in_turn(vec![
            ("201 Created", STARTED.to_string()),
            ("200 OK", r#"{"state":"pending"}"#.to_string()),
            ("200 OK", r#"{"state":"pending"}"#.to_string()),
            (
                "200 OK",
                r#"{"state":"approved","key":"mnm_the-key","machine":"gaming-pc","kind":"intermittent"}"#.to_string(),
            ),
        ]);
        let shown = std::cell::RefCell::new(Vec::new());
        let waits = std::cell::Cell::new(0u32);

        let outcome = link_by_asking(
            &address,
            "Gaming PC",
            &|request| shown.borrow_mut().push(request.clone()),
            &|every| {
                assert_eq!(every, Duration::from_secs(3));
                waits.set(waits.get() + 1);
            },
        )
        .unwrap();

        assert_eq!(
            outcome,
            LinkOutcome::Linked(Enrolled {
                key: "mnm_the-key".to_string(),
                machine: "gaming-pc".to_string(),
                kind: "intermittent".to_string()
            })
        );
        assert_eq!(waits.get(), 3);
        let shown = shown.into_inner();
        assert_eq!(shown.len(), 1);
        assert_eq!(shown[0].user_code, "ABCD-EFGH-JKLM");
        assert_eq!(
            shown[0].verification_url,
            "https://m.example/link?code=ABCD-EFGH-JKLM"
        );
        let asked = handle.join().unwrap();
        assert!(asked[0].contains("POST /api/v1/ingest/pair "));
        assert!(asked[0].contains("Gaming PC"));
        assert!(asked[1].contains("/api/v1/ingest/pair/poll") && asked[1].contains("dvc_secret"));
        assert!(asked.iter().all(|request| {
            request
                .to_ascii_lowercase()
                .contains("x-mnemarr-request: 1")
        }));
        assert!(
            asked
                .iter()
                .all(|request| !request.to_ascii_lowercase().contains("x-api-key")),
            "no key yet"
        );
    }

    #[test]
    fn should_say_when_a_person_refused_or_the_request_expired() {
        for (state, expected) in [
            ("rejected", LinkOutcome::Rejected),
            ("expired", LinkOutcome::Expired),
        ] {
            let (address, handle) = serve_in_turn(vec![
                ("201 Created", STARTED.to_string()),
                ("200 OK", format!(r#"{{"state":"{state}"}}"#)),
            ]);

            let outcome = link_by_asking(&address, "pc", &|_| {}, &|_| {}).unwrap();

            assert_eq!(outcome, expected);
            handle.join().unwrap();
        }
    }

    #[test]
    fn should_go_on_asking_when_the_server_cannot_be_reached_for_a_moment_and_stop_when_it_refuses()
    {
        let (address, handle) = serve_in_turn(vec![
            ("201 Created", STARTED.to_string()),
            ("503 Service Unavailable", "{}".to_string()),
            ("200 OK", r#"{"state":"rejected"}"#.to_string()),
        ]);
        assert_eq!(
            link_by_asking(&address, "pc", &|_| {}, &|_| {}).unwrap(),
            LinkOutcome::Rejected
        );
        handle.join().unwrap();

        let (address, handle) = serve_in_turn(vec![
            ("201 Created", STARTED.to_string()),
            (
                "400 Bad Request",
                r#"{"title":"The request is not valid."}"#.to_string(),
            ),
        ]);
        assert_eq!(
            link_by_asking(&address, "pc", &|_| {}, &|_| {}).unwrap_err(),
            UploadError::Refused(400, "The request is not valid.".to_string())
        );
        handle.join().unwrap();
    }

    #[test]
    fn should_give_up_and_say_it_expired_when_nobody_answers_before_the_time_is_over() {
        let mut answers = vec![("201 Created", STARTED.to_string())];
        answers.extend((0..300).map(|_| ("200 OK", r#"{"state":"pending"}"#.to_string())));
        let (address, _handle) = serve_in_turn(answers);
        let waited = std::cell::Cell::new(Duration::ZERO);

        let outcome = link_by_asking(&address, "pc", &|_| {}, &|every| {
            waited.set(waited.get() + every)
        })
        .unwrap();

        assert_eq!(outcome, LinkOutcome::Expired);
        assert!(
            waited.get() >= Duration::from_secs(600),
            "it waited the whole time"
        );
        assert!(waited.get() <= Duration::from_secs(610));
    }

    #[test]
    fn should_not_take_a_start_or_an_answer_that_is_not_the_one_expected() {
        let (address, handle) =
            serve_in_turn(vec![("201 Created", r#"{"userCode":"X"}"#.to_string())]);
        assert!(matches!(
            pair_start(&address, "pc"),
            Err(UploadError::Later(_))
        ));
        handle.join().unwrap();

        let (address, handle) = serve_in_turn(vec![(
            "200 OK",
            r#"{"state":"approved","machine":"pc"}"#.to_string(),
        )]);
        assert!(
            matches!(pair_poll(&address, "dvc"), Err(UploadError::Later(_))),
            "approved without a key"
        );
        handle.join().unwrap();
    }
}
