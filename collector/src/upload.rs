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

/// Where batches are sent: Mnemarr, or whatever a test puts in its place.
pub trait Uploader {
    fn send(&self, batch: &Batch<'_>) -> Result<Reply, UploadError>;
}

/// Sends batches to a Mnemarr server over HTTP, with the ingest key of the machine.
pub struct HttpUploader {
    url: String,
    key: String,
    agent: ureq::Agent,
}

impl HttpUploader {
    pub fn new(server: &str, key: &str) -> Self {
        let agent: ureq::Agent = ureq::Agent::config_builder()
            .timeout_global(Some(Duration::from_secs(60)))
            .http_status_as_error(false)
            // The key is in a header that is not the standard one: a redirect must not carry it to another host.
            .max_redirects(0)
            .user_agent(format!("mnemarr-collector/{}", crate::VERSION))
            .build()
            .into();
        Self {
            url: format!(
                "{}/api/v1/ingest/conversations",
                server.trim_end_matches('/')
            ),
            key: key.to_string(),
            agent,
        }
    }
}

impl Uploader for HttpUploader {
    fn send(&self, batch: &Batch<'_>) -> Result<Reply, UploadError> {
        let body = json!({
            "format": batch.format,
            "machine": batch.machine,
            "application": batch.application,
            "session": batch.session,
            "from": batch.from,
            "lines": batch.lines,
        });
        let mut response = self
            .agent
            .post(&self.url)
            .header("X-Api-Key", &self.key)
            .send_json(body)
            .map_err(|error| UploadError::Later(error.to_string()))?;
        let status = response.status().as_u16();
        if status == 200 {
            let reply: Value = response
                .body_mut()
                .read_json()
                .map_err(|error| UploadError::Later(error.to_string()))?;
            return match (
                reply.get("next").and_then(Value::as_u64),
                reply.get("accepted").and_then(Value::as_u64),
            ) {
                (Some(next), Some(accepted)) => Ok(Reply { next, accepted }),
                _ => Err(UploadError::Later(
                    "the answer is not the one expected".to_string(),
                )),
            };
        }
        let reason = response.body_mut().read_to_string().unwrap_or_default();
        let reason: String = reason.chars().take(300).collect();
        match status {
            // Busy, or a server that is starting or failing: a later pass tries again.
            408 | 425 | 429 | 500..=599 => Err(UploadError::Later(format!("status {status}"))),
            _ => Err(UploadError::Refused(status, reason)),
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
        for status in ["429 Too Many Requests", "503 Service Unavailable"] {
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
    fn should_not_follow_a_redirect_with_the_key_and_should_name_itself_and_its_version() {
        let (address, handle) = serve_once("302 Found", "{}");
        let lines = vec!["{}".to_string()];

        let error = HttpUploader::new(&address, "the-key")
            .send(&batch(&lines))
            .unwrap_err();

        assert!(matches!(error, UploadError::Refused(302, _)));
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
}
