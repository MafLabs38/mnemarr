//! Sends a batch of lines to Mnemarr (ADR 0024).

use std::time::Duration;

use serde_json::{Value, json};

/// New lines of one conversation, as the server takes them.
pub struct Batch<'a> {
    /// What the lines are (the name of the reader that made them): the server has a parser for each.
    pub format: &'a str,
    pub machine: &'a str,
    pub application: &'a str,
    pub session: &'a str,
    /// The position, in the file, of the first line.
    pub from: u64,
    pub lines: &'a [String],
}

/// What the server took: how many lines, and the position of the next line it wants.
#[derive(Debug, PartialEq, Eq)]
pub struct Reply {
    pub next: u64,
    pub accepted: u64,
}

/// Why a batch did not go.
#[derive(Debug, PartialEq, Eq)]
pub enum UploadError {
    /// The server did not answer, or is busy: nothing is lost, a later pass sends it again.
    Later(String),
    /// The server answered that it will not take this: a key that is refused, a batch that is not one.
    Refused(u16, String),
}

impl std::fmt::Display for UploadError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Later(reason) => write!(f, "the server could not be reached: {reason}"),
            Self::Refused(status, reason) => write!(f, "the server refused ({status}): {reason}"),
        }
    }
}

impl std::error::Error for UploadError {}

/// What a collector says of itself at the end of a pass (ADR 0027 §6): that it is there, which version it is, what it still has to send, and, when a
/// file could not be read or a conversation was refused, a short sentence with no path and no content.
pub struct Heartbeat<'a> {
    pub version: &'a str,
    pub pending_lines: u64,
    pub error: Option<&'a str>,
    /// A person asked it to be quiet: it still says it is there, and says why it sends nothing.
    pub paused: bool,
    /// How often a pass is made: a Mnemarr takes the machine to be sending while its last sign of life is younger than three passes.
    pub interval_seconds: u64,
    /// The program was installed with the installer and can replace itself.
    pub can_update: bool,
}

/// Where batches are sent: Mnemarr, or whatever a test puts in its place.
pub trait Uploader {
    fn send(&self, batch: &Batch<'_>) -> Result<Reply, UploadError>;

    /// The sign of life at the end of a pass. A test that does not look at it need not say anything.
    fn heartbeat(&self, _beat: &Heartbeat<'_>) -> Result<(), UploadError> {
        Ok(())
    }
}

/// The client every call uses: no redirect is followed (the key is in a header that is not the standard one, and the code is a secret), and it
/// names itself and its version.
pub(crate) fn agent() -> ureq::Agent {
    ureq::Agent::config_builder()
        .timeout_global(Some(Duration::from_secs(60)))
        .http_status_as_error(false)
        .max_redirects(0)
        .user_agent(format!("mnemarr-collector/{}", crate::VERSION))
        .build()
        .into()
}

/// Reads why the server refused: the title of its problem document, or the first words of the body.
pub(crate) fn reason_of(body: &str) -> String {
    let text = serde_json::from_str::<Value>(body)
        .ok()
        .and_then(|value| {
            value
                .get("title")
                .and_then(Value::as_str)
                .map(str::to_string)
        })
        .unwrap_or_else(|| body.to_string());
    text.chars().take(300).collect()
}

/// Sends batches to a Mnemarr server over HTTP, with the ingest key of the machine.
pub struct HttpUploader {
    base: String,
    key: String,
    agent: ureq::Agent,
}

impl HttpUploader {
    pub fn new(server: &str, key: &str) -> Self {
        Self {
            base: format!("{}/api/v1/ingest", server.trim_end_matches('/')),
            key: key.to_string(),
            agent: agent(),
        }
    }

    /// Posts a document with the key, and gives back the status and the body of the answer.
    fn post(&self, path: &str, body: Value) -> Result<(u16, String), UploadError> {
        let mut response = self
            .agent
            .post(format!("{}/{path}", self.base))
            .header("X-Api-Key", &self.key)
            .send_json(body)
            .map_err(|error| UploadError::Later(error.to_string()))?;
        let status = response.status().as_u16();
        let text = response.body_mut().read_to_string().unwrap_or_default();
        Ok((status, text))
    }
}

/// A status that is not a success, as the collector tells it: a later pass tries again, or the server will not take this.
fn failure(status: u16, body: &str) -> UploadError {
    match status {
        // Busy, or a server that is starting or failing: a later pass tries again.
        408 | 425 | 429 | 500..=599 => UploadError::Later(format!("status {status}")),
        _ => UploadError::Refused(status, reason_of(body)),
    }
}

impl Uploader for HttpUploader {
    fn send(&self, batch: &Batch<'_>) -> Result<Reply, UploadError> {
        let (status, text) = self.post(
            "conversations",
            json!({
                "format": batch.format,
                "machine": batch.machine,
                "application": batch.application,
                "session": batch.session,
                "from": batch.from,
                "lines": batch.lines,
            }),
        )?;
        if status != 200 {
            return Err(failure(status, &text));
        }
        let reply: Value =
            serde_json::from_str(&text).map_err(|error| UploadError::Later(error.to_string()))?;
        match (
            reply.get("next").and_then(Value::as_u64),
            reply.get("accepted").and_then(Value::as_u64),
        ) {
            (Some(next), Some(accepted)) => Ok(Reply { next, accepted }),
            _ => Err(UploadError::Later(
                "the answer is not the one expected".to_string(),
            )),
        }
    }

    fn heartbeat(&self, beat: &Heartbeat<'_>) -> Result<(), UploadError> {
        let (status, text) = self.post(
            "heartbeat",
            json!({
                "version": beat.version,
                "pendingLines": beat.pending_lines,
                "error": beat.error,
                "paused": beat.paused,
                "platform": crate::computer::platform(),
                "program": crate::computer::program_hash(),
                "computerId": crate::computer::id(),
                "intervalSeconds": beat.interval_seconds,
                "canUpdate": beat.can_update,
            }),
        )?;
        if status == 200 || status == 204 {
            Ok(())
        } else {
            Err(failure(status, &text))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{Read, Write};
    use std::net::TcpListener;

    /// A server that answers one request with the given status and body, and gives back the request it got.
    fn serve_once(status: &str, body: &str) -> (String, std::thread::JoinHandle<String>) {
        serve_once_waiting(status, "", body, std::time::Duration::from_secs(10))
    }

    /// Answers one request and returns what it received; when nobody connects before the deadline it returns an empty text, so that a
    /// regression fails an assertion instead of hanging the build.
    fn serve_once_waiting(
        status: &str,
        headers: &str,
        body: &str,
        wait: std::time::Duration,
    ) -> (String, std::thread::JoinHandle<String>) {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        listener.set_nonblocking(true).unwrap();
        let address = format!("http://{}", listener.local_addr().unwrap());
        let answer = format!(
            "HTTP/1.1 {status}\r\nContent-Type: application/json\r\n{headers}Content-Length: {}\r\nConnection: close\r\n\r\n{body}",
            body.len()
        );
        let handle = std::thread::spawn(move || {
            let deadline = std::time::Instant::now() + wait;
            let mut stream = loop {
                match listener.accept() {
                    Ok((stream, _)) => break stream,
                    Err(_) if std::time::Instant::now() < deadline => {
                        std::thread::sleep(std::time::Duration::from_millis(10));
                    }
                    Err(_) => return String::new(),
                }
            };
            stream.set_nonblocking(false).unwrap();
            let mut request = Vec::new();
            let mut chunk = [0u8; 4096];
            loop {
                let read = stream.read(&mut chunk).unwrap();
                request.extend_from_slice(&chunk[..read]);
                let text = String::from_utf8_lossy(&request).to_string();
                if let Some(split) = text.find("\r\n\r\n") {
                    let length = text
                        .lines()
                        .find_map(|line| {
                            line.to_ascii_lowercase()
                                .strip_prefix("content-length: ")
                                .and_then(|value| value.trim().parse::<usize>().ok())
                        })
                        .unwrap_or(0);
                    if request.len() >= split + 4 + length {
                        break;
                    }
                }
                if read == 0 {
                    break;
                }
            }
            stream.write_all(answer.as_bytes()).unwrap();
            String::from_utf8_lossy(&request).to_string()
        });
        (address, handle)
    }

    fn batch(lines: &[String]) -> Batch<'_> {
        Batch {
            format: "claude-code",
            machine: "pc",
            application: "mnemarr",
            session: "s1",
            from: 4,
            lines,
        }
    }

    #[test]
    fn should_send_the_lines_with_the_key_and_read_where_the_server_is() {
        let (address, handle) = serve_once("200 OK", r#"{"next":6,"accepted":2}"#);
        let lines = vec!["{\"a\":1}".to_string(), "{\"b\":2}".to_string()];

        let reply = HttpUploader::new(&address, "the-key")
            .send(&batch(&lines))
            .unwrap();

        assert_eq!(
            reply,
            Reply {
                next: 6,
                accepted: 2
            }
        );
        let request = handle.join().unwrap();
        assert!(request.starts_with("POST /api/v1/ingest/conversations "));
        assert!(request.to_ascii_lowercase().contains("x-api-key: the-key"));
        let body: Value =
            serde_json::from_str(&request[request.find("\r\n\r\n").unwrap() + 4..]).unwrap();
        assert_eq!(
            (body["from"].as_u64(), body["session"].as_str()),
            (Some(4), Some("s1"))
        );
        assert_eq!(body["lines"].as_array().unwrap().len(), 2);
    }

    #[test]
    fn should_try_again_later_when_the_server_is_busy_or_failing() {
        for status in [
            "408 Request Timeout",
            "429 Too Many Requests",
            "503 Service Unavailable",
        ] {
            let (address, handle) = serve_once(status, "{}");
            let lines = vec!["{}".to_string()];

            let error = HttpUploader::new(&address, "k")
                .send(&batch(&lines))
                .unwrap_err();

            assert!(matches!(error, UploadError::Later(_)), "{status}");
            handle.join().unwrap();
        }
    }

    #[test]
    fn should_not_panic_and_try_again_later_when_the_server_answers_with_something_that_is_not_a_reply()
     {
        for body in ["", "not json", "{\"next\":\"x\"}"] {
            let (address, handle) = serve_once("200 OK", body);
            let lines = vec!["{}".to_string()];

            let error = HttpUploader::new(&address, "k")
                .send(&batch(&lines))
                .unwrap_err();

            assert!(matches!(error, UploadError::Later(_)), "{body:?}");
            handle.join().unwrap();
        }
    }

    #[test]
    fn should_not_follow_a_redirect_and_so_never_send_the_key_to_where_it_points() {
        let (elsewhere, watcher) =
            serve_once_waiting("200 OK", "", "{}", std::time::Duration::from_millis(250));
        let (address, handle) = serve_once_waiting(
            "302 Found",
            &format!("Location: {elsewhere}\r\n"),
            "{}",
            std::time::Duration::from_secs(10),
        );
        let lines = vec!["{}".to_string()];

        let error = HttpUploader::new(&address, "the-key")
            .send(&batch(&lines))
            .unwrap_err();

        assert!(matches!(error, UploadError::Refused(302, _)));
        handle.join().unwrap();
        let received = watcher.join().unwrap().to_ascii_lowercase();
        assert!(
            !received.contains("the-key"),
            "the redirect target was sent the key: {received}"
        );
    }

    #[test]
    fn should_name_itself_and_its_version() {
        let (address, handle) = serve_once("200 OK", "{\"next\":1,\"accepted\":1}");
        let lines = vec!["{}".to_string()];

        HttpUploader::new(&address, "k")
            .send(&batch(&lines))
            .unwrap();

        let request = handle.join().unwrap().to_ascii_lowercase();
        assert!(request.contains(&format!("user-agent: mnemarr-collector/{}", crate::VERSION)));
    }

    #[test]
    fn should_say_that_a_key_or_a_batch_is_refused_and_not_try_again() {
        for status in ["401 Unauthorized", "403 Forbidden", "400 Bad Request"] {
            let (address, handle) = serve_once(status, "{\"title\":\"no\"}");
            let lines = vec!["{}".to_string()];

            let error = HttpUploader::new(&address, "k")
                .send(&batch(&lines))
                .unwrap_err();

            assert!(matches!(error, UploadError::Refused(_, _)), "{status}");
            handle.join().unwrap();
        }
    }

    #[test]
    fn should_try_again_later_when_nobody_answers() {
        let lines = vec!["{}".to_string()];
        // A port that was free a moment ago, and where nobody listens.
        let address = {
            let listener = TcpListener::bind("127.0.0.1:0").unwrap();
            format!("http://{}", listener.local_addr().unwrap())
        };

        let error = HttpUploader::new(&address, "k")
            .send(&batch(&lines))
            .unwrap_err();

        assert!(matches!(error, UploadError::Later(_)));
    }

    #[test]
    fn should_send_a_sign_of_life_with_the_key_the_version_and_what_is_left() {
        let (address, handle) = serve_once("204 No Content", "");

        HttpUploader::new(&address, "the-key")
            .heartbeat(&Heartbeat {
                version: "0.2.0",
                pending_lines: 7,
                error: Some("1 file(s) could not be read"),
                paused: true,
                interval_seconds: 30,
                can_update: false,
            })
            .unwrap();

        let request = handle.join().unwrap();
        assert!(request.starts_with("POST /api/v1/ingest/heartbeat"));
        assert!(request.to_ascii_lowercase().contains("x-api-key: the-key"));
        let body: Value = serde_json::from_str(request.split("\r\n\r\n").nth(1).unwrap()).unwrap();
        assert_eq!(
            body,
            json!({
                "version": "0.2.0",
                "pendingLines": 7,
                "error": "1 file(s) could not be read",
                "paused": true,
                // What this program is, so that a Mnemarr can tell whether it carries another one (ADR 0031 §7).
                "platform": crate::computer::platform(),
                "program": crate::computer::program_hash(),
                "computerId": crate::computer::id(),
                "intervalSeconds": 30,
                "canUpdate": false,
            })
        );
    }

    #[test]
    fn should_say_that_a_key_is_refused_by_the_sign_of_life_and_try_again_later_when_the_server_fails()
     {
        let beat = Heartbeat {
            version: "0.2.0",
            pending_lines: 0,
            error: None,
            paused: false,
            interval_seconds: 30,
            can_update: false,
        };
        let (address, handle) = serve_once("401 Unauthorized", "{\"title\":\"no\"}");
        assert!(matches!(
            HttpUploader::new(&address, "old").heartbeat(&beat),
            Err(UploadError::Refused(401, _))
        ));
        handle.join().unwrap();

        let (address, handle) = serve_once("503 Service Unavailable", "{}");
        assert!(matches!(
            HttpUploader::new(&address, "k").heartbeat(&beat),
            Err(UploadError::Later(_))
        ));
        handle.join().unwrap();
    }

    #[test]
    fn should_read_the_reason_of_a_problem_document_or_the_first_words_of_anything_else() {
        assert_eq!(
            reason_of(r#"{"title":"The code is not valid."}"#),
            "The code is not valid."
        );
        assert_eq!(reason_of("a page of a proxy"), "a page of a proxy");
        assert_eq!(reason_of(&"x".repeat(1000)).len(), 300);
    }
}
