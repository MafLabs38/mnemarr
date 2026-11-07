//! The page on this computer where a person links the collector to a Mnemarr (ADR 0030), opened from the icon.
//!
//! It is a few lines of HTML served on the loopback address of this computer only, for a short time, under an address nobody can guess. The person types the
//! address of a Mnemarr; the collector asks it to be linked, the browser opens the page of that Mnemarr where the person approves, and this page shows how
//! it goes. It needs no program beyond the collector and puts nothing on the network.

use std::collections::hash_map::RandomState;
use std::hash::{BuildHasher, Hasher};
use std::io::{self, Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

use crate::browser;
use crate::config::check_server;
use crate::destinations;
use crate::enrol::{self, LinkOutcome, PairingRequest};

/// How long the page lives: long enough to link, short enough that it is not left open.
const LIFE: Duration = Duration::from_secs(30 * 60);
/// The most a request of this page can be.
const MAX_REQUEST: usize = 8192;

/// How the linking goes, as the page tells it.
#[derive(Debug, Clone, PartialEq, Eq)]
enum Progress {
    Idle,
    Asking,
    Waiting {
        code: String,
        address: String,
    },
    Linked {
        destination: String,
        machine: String,
    },
    Failed(String),
}

/// What the page needs from outside: where the collector keeps its files, what it is called, how to open an address, and how time passes.
pub struct Environment {
    pub dir: PathBuf,
    pub hostname: String,
    pub open: Arc<dyn Fn(&str) + Send + Sync>,
    pub wait: Arc<dyn Fn(Duration) + Send + Sync>,
}

/// A page that is being served.
pub struct LinkPage {
    /// The address to open in the browser, with its secret in it.
    pub address: String,
    stop: Arc<AtomicBool>,
}

impl LinkPage {
    /// Ends the page now.
    pub fn stop(&self) {
        self.stop.store(true, Ordering::SeqCst);
    }
}

/// An unguessable text: the keys of the hasher of the standard library come from the system, and are different for each one.
fn secret() -> String {
    (0..4)
        .map(|_| format!("{:016x}", RandomState::new().build_hasher().finish()))
        .collect()
}

/// Serves the page in a thread of its own, on a free port of the loopback address, and says where.
pub fn start(environment: Environment) -> io::Result<LinkPage> {
    let listener = TcpListener::bind("127.0.0.1:0")?;
    listener.set_nonblocking(true)?;
    let port = listener.local_addr()?.port();
    let token = secret();
    let address = format!("http://127.0.0.1:{port}/{token}/");
    let stop = Arc::new(AtomicBool::new(false));
    let page = Page {
        port,
        token,
        environment,
        progress: Arc::new(Mutex::new(Progress::Idle)),
    };
    let ending = stop.clone();
    thread::spawn(move || {
        let deadline = Instant::now() + LIFE;
        while !ending.load(Ordering::SeqCst) && Instant::now() < deadline {
            match listener.accept() {
                Ok((stream, _)) => page.serve(stream),
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => {
                    thread::sleep(Duration::from_millis(40));
                }
                Err(_) => break,
            }
        }
    });
    Ok(LinkPage { address, stop })
}

struct Page {
    port: u16,
    token: String,
    environment: Environment,
    progress: Arc<Mutex<Progress>>,
}

/// What a request said.
struct Request {
    method: String,
    path: String,
    host: String,
    body: String,
}

fn read_request(stream: &mut TcpStream) -> Option<Request> {
    stream.set_nonblocking(false).ok()?;
    stream.set_read_timeout(Some(Duration::from_secs(5))).ok()?;
    let mut data = Vec::new();
    let mut chunk = [0u8; 1024];
    let (head_end, length) = loop {
        let read = stream.read(&mut chunk).ok()?;
        if read == 0 {
            return None;
        }
        data.extend_from_slice(chunk.get(..read)?);
        if data.len() > MAX_REQUEST {
            return None;
        }
        let text = String::from_utf8_lossy(&data).to_string();
        if let Some((head, _)) = text.split_once("\r\n\r\n") {
            let length = head
                .lines()
                .find_map(|line| {
                    let (name, value) = line.split_once(':')?;
                    name.eq_ignore_ascii_case("content-length")
                        .then(|| value.trim().parse::<usize>().ok())?
                })
                .unwrap_or(0);
            break (head.len() + 4, length);
        }
    };
    if length > MAX_REQUEST {
        return None;
    }
    while data.len() < head_end + length {
        let read = stream.read(&mut chunk).ok()?;
        if read == 0 {
            return None;
        }
        data.extend_from_slice(chunk.get(..read)?);
    }
    let text = String::from_utf8_lossy(&data).to_string();
    let (head, _) = text.split_once("\r\n\r\n")?;
    let mut lines = head.lines();
    let mut first = lines.next()?.split_whitespace();
    let (method, path) = (first.next()?.to_string(), first.next()?.to_string());
    let host = lines
        .find_map(|line| {
            let (name, value) = line.split_once(':')?;
            name.eq_ignore_ascii_case("host")
                .then(|| value.trim().to_ascii_lowercase())
        })
        .unwrap_or_default();
    Some(Request {
        method,
        path,
        host,
        body: String::from_utf8_lossy(data.get(head_end..head_end + length)?).to_string(),
    })
}

/// Escapes text for HTML: what is shown here may come from a server.
fn escape(text: &str) -> String {
    text.chars()
        .map(|c| match c {
            '&' => "&amp;".to_string(),
            '<' => "&lt;".to_string(),
            '>' => "&gt;".to_string(),
            '"' => "&quot;".to_string(),
            '\'' => "&#39;".to_string(),
            other => other.to_string(),
        })
        .collect()
}

/// The value of a field of a form that the browser sent (`a=b&c=d`, with `+` and `%xx`).
fn field(form: &str, name: &str) -> Option<String> {
    form.split('&').find_map(|pair| {
        let (key, value) = pair.split_once('=')?;
        (key == name).then(|| decode(value))
    })
}

fn decode(value: &str) -> String {
    let bytes = value.as_bytes();
    let mut out = Vec::new();
    let mut index = 0;
    while let Some(&byte) = bytes.get(index) {
        match byte {
            b'+' => out.push(b' '),
            b'%' => {
                let hex = bytes
                    .get(index + 1..index + 3)
                    .and_then(|pair| std::str::from_utf8(pair).ok())
                    .and_then(|pair| u8::from_str_radix(pair, 16).ok());
                if let Some(decoded) = hex {
                    out.push(decoded);
                    index += 2;
                } else {
                    out.push(byte);
                }
            }
            other => out.push(other),
        }
        index += 1;
    }
    String::from_utf8_lossy(&out).to_string()
}

fn respond(stream: &mut TcpStream, status: &str, extra: &str, body: &str) {
    let text = format!(
        "HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; form-action 'self'\r\nConnection: close\r\n{extra}\r\n{body}",
        body.len()
    );
    let _ = stream.write_all(text.as_bytes());
}

fn document(refresh: bool, content: &str) -> String {
    let meta = if refresh {
        "<meta http-equiv=\"refresh\" content=\"2\">"
    } else {
        ""
    };
    format!(
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">{meta}<title>Link to a Mnemarr</title>\
<style>body{{font:16px system-ui,sans-serif;max-width:34rem;margin:3rem auto;padding:0 1rem;color:#1d2433}}\
input[type=text]{{width:100%;padding:.5rem;margin:.3rem 0 1rem;font-size:1rem}}button{{padding:.55rem 1.1rem;font-size:1rem}}\
.error{{color:#8a1c1c}}code{{font-size:1.3rem;letter-spacing:.08rem}}</style></head><body><h1>Link to a Mnemarr</h1>{content}</body></html>"
    )
}

fn form(problem: Option<&str>) -> String {
    let error = problem.map_or_else(String::new, |text| {
        format!("<p class=\"error\" role=\"alert\">{}</p>", escape(text))
    });
    document(
        false,
        &format!(
            "<p>Type the address of the Mnemarr to send this computer's conversations to. You will then approve this computer in that Mnemarr.</p>{error}\
<form method=\"post\" action=\"link\"><label for=\"server\">Address of Mnemarr</label>\
<input id=\"server\" name=\"server\" type=\"text\" placeholder=\"https://mnemarr.example\" required autofocus>\
<p><label><input name=\"insecure\" type=\"checkbox\" value=\"1\"> This address is plain http:// on my own network (the key will travel without encryption)</label></p>\
<button type=\"submit\">Link</button></form>"
        ),
    )
}

impl Page {
    fn serve(&self, mut stream: TcpStream) {
        let Some(request) = read_request(&mut stream) else {
            respond(
                &mut stream,
                "400 Bad Request",
                "",
                &document(false, "<p>Bad request.</p>"),
            );
            return;
        };
        // A page of another site that makes the browser call this address cannot be told apart from a request by its host name once the name is made to
        // point here: only the two names of this computer are served.
        let own = [
            format!("127.0.0.1:{}", self.port),
            format!("localhost:{}", self.port),
        ];
        if !own.contains(&request.host) {
            respond(
                &mut stream,
                "400 Bad Request",
                "",
                &document(false, "<p>Bad request.</p>"),
            );
            return;
        }
        let Some(route) = request.path.strip_prefix(&format!("/{}/", self.token)) else {
            respond(
                &mut stream,
                "404 Not Found",
                "",
                &document(false, "<p>Not found.</p>"),
            );
            return;
        };
        match (request.method.as_str(), route) {
            ("GET", "") => respond(&mut stream, "200 OK", "", &form(None)),
            ("GET", "status") => {
                let body = self.status_page();
                let refresh = matches!(self.state(), Progress::Asking | Progress::Waiting { .. });
                respond(&mut stream, "200 OK", "", &document(refresh, &body));
            }
            ("POST", "link") => self.start_linking(&mut stream, &request.body),
            _ => respond(
                &mut stream,
                "404 Not Found",
                "",
                &document(false, "<p>Not found.</p>"),
            ),
        }
    }

    fn state(&self) -> Progress {
        self.progress
            .lock()
            .map_or(Progress::Idle, |progress| progress.clone())
    }

    fn status_page(&self) -> String {
        match self.state() {
            Progress::Idle => "<p>Nothing was asked. <a href=\"./\">Start</a></p>".to_string(),
            Progress::Asking => "<p>Asking Mnemarr…</p>".to_string(),
            Progress::Waiting { code, address } => format!(
                "<p>Waiting for you to approve this computer in Mnemarr. A page was opened in your browser; if not, open <a href=\"{0}\">{0}</a>.</p><p>The code it shows: <code>{1}</code></p>",
                escape(&address),
                escape(&code)
            ),
            Progress::Linked {
                destination,
                machine,
            } => format!(
                "<p>Linked to <strong>{}</strong> as <strong>{}</strong>. This computer starts sending its conversations there at its next pass. You can close this page.</p>",
                escape(&destination),
                escape(&machine)
            ),
            Progress::Failed(reason) => format!(
                "<p class=\"error\" role=\"alert\">{}</p><p><a href=\"./\">Try again</a></p>",
                escape(&reason)
            ),
        }
    }

    fn start_linking(&self, stream: &mut TcpStream, body: &str) {
        let server = field(body, "server")
            .unwrap_or_default()
            .trim()
            .trim_end_matches('/')
            .to_string();
        let insecure = field(body, "insecure").is_some();
        if let Err(error) = check_server(&server, insecure) {
            let text = if server.is_empty() {
                "Type the address of the Mnemarr.".to_string()
            } else {
                error.to_string()
            };
            respond(stream, "200 OK", "", &form(Some(&text)));
            return;
        }
        if matches!(self.state(), Progress::Asking | Progress::Waiting { .. }) {
            respond(stream, "303 See Other", "Location: status\r\n", "");
            return;
        }
        self.set(Progress::Asking);
        let progress = self.progress.clone();
        let dir = self.environment.dir.clone();
        let hostname = self.environment.hostname.clone();
        let open = self.environment.open.clone();
        let wait = self.environment.wait.clone();
        thread::spawn(move || {
            let set = |value: Progress| {
                if let Ok(mut current) = progress.lock() {
                    *current = value;
                }
            };
            let show = |request: &PairingRequest| {
                set(Progress::Waiting {
                    code: request.user_code.clone(),
                    address: request.verification_url.clone(),
                });
                if browser::is_same_server(&server, &request.verification_url) {
                    open(&request.verification_url);
                }
            };
            let outcome = enrol::link_by_asking(&server, &hostname, &show, &|every| wait(every));
            set(match outcome {
                Ok(LinkOutcome::Linked(enrolled)) => {
                    match destinations::add(
                        &dir,
                        &server,
                        &enrolled.machine,
                        insecure,
                        None,
                        &enrolled.key,
                    ) {
                        Ok(destination) => Progress::Linked {
                            destination: destination.name,
                            machine: enrolled.machine,
                        },
                        Err(error) => Progress::Failed(format!(
                            "Linked, but the key could not be kept: {error}. Ask again."
                        )),
                    }
                }
                Ok(LinkOutcome::Rejected) => {
                    Progress::Failed("A person refused this computer in Mnemarr.".to_string())
                }
                Ok(LinkOutcome::Expired) => {
                    Progress::Failed("Nobody answered in time. Start again.".to_string())
                }
                Err(error) => Progress::Failed(format!("Mnemarr could not be asked: {error}")),
            });
        });
        respond(stream, "303 See Other", "Location: status\r\n", "");
    }

    fn set(&self, value: Progress) {
        if let Ok(mut current) = self.progress.lock() {
            *current = value;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::TcpListener;
    use std::sync::Mutex as StdMutex;

    /// A server that answers the requests it gets in turn.
    fn serve_in_turn(answers: Vec<(&'static str, String)>) -> (String, thread::JoinHandle<usize>) {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let address = format!("http://{}", listener.local_addr().unwrap());
        let address_for_answers = address.clone();
        let handle = thread::spawn(move || {
            let mut count = 0;
            for (status, body) in answers {
                // "{server}" in an answer stands for the address of this fake Mnemarr: what a real one writes in the address to open.
                let body = body.replace("{server}", &address_for_answers);
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
                count += 1;
                let answer = format!(
                    "HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                    body.len()
                );
                stream.write_all(answer.as_bytes()).unwrap();
            }
            count
        });
        (address, handle)
    }

    const STARTED: &str = r#"{"userCode":"ABCD-EFGH-JKLM","deviceCode":"dvc_secret","verificationUrl":"https://m.example/link?code=ABCD-EFGH-JKLM","expiresInSeconds":600,"intervalSeconds":3}"#;

    struct Served {
        page: LinkPage,
        opened: Arc<StdMutex<Vec<String>>>,
        dir: PathBuf,
    }

    impl Drop for Served {
        fn drop(&mut self) {
            self.page.stop();
            let _ = std::fs::remove_dir_all(&self.dir);
        }
    }

    fn serve(name: &str) -> Served {
        let dir = std::env::temp_dir().join(format!("mnemarr-page-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let opened = Arc::new(StdMutex::new(Vec::new()));
        let seen = opened.clone();
        let page = start(Environment {
            dir: dir.clone(),
            hostname: "Gaming PC".to_string(),
            open: Arc::new(move |address| seen.lock().unwrap().push(address.to_string())),
            wait: Arc::new(|_| thread::sleep(Duration::from_millis(5))),
        })
        .unwrap();
        Served { page, opened, dir }
    }

    /// Sends a request to the page by hand: (status line, headers and body).
    fn send(
        address: &str,
        path_after_token: &str,
        method: &str,
        host: Option<&str>,
        body: &str,
    ) -> (String, String) {
        let rest = address.strip_prefix("http://").unwrap();
        let (authority, path) = rest.split_once('/').unwrap();
        let token = path.trim_end_matches('/');
        let mut stream = TcpStream::connect(authority).unwrap();
        let request = format!(
            "{method} /{token}/{path_after_token} HTTP/1.1\r\nHost: {}\r\nContent-Type: application/x-www-form-urlencoded\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
            host.unwrap_or(authority),
            body.len()
        );
        stream.write_all(request.as_bytes()).unwrap();
        let mut answer = String::new();
        stream.read_to_string(&mut answer).unwrap();
        let (head, rest) = answer.split_once("\r\n\r\n").unwrap_or((&answer, ""));
        (
            head.lines().next().unwrap_or("").to_string(),
            format!("{head}\r\n\r\n{rest}"),
        )
    }

    fn form_for(server: &str) -> String {
        format!("server={}", server.replace(':', "%3A").replace('/', "%2F"))
    }

    #[test]
    fn should_serve_the_form_only_under_its_secret_to_this_computer_with_headers_that_keep_it_closed()
     {
        let served = serve("form");
        let address = &served.page.address;

        let (status, text) = send(address, "", "GET", None, "");
        assert_eq!(status, "HTTP/1.1 200 OK");
        assert!(text.contains("Address of Mnemarr") && text.contains("<form"));
        let lower = text.to_ascii_lowercase();
        assert!(lower.contains("content-security-policy: default-src 'none'"));
        assert!(
            lower.contains("cache-control: no-store")
                && lower.contains("x-content-type-options: nosniff")
        );

        let wrong = address.replace(
            address.trim_end_matches('/').rsplit('/').next().unwrap(),
            "not-the-secret",
        );
        assert_eq!(
            send(&wrong, "", "GET", None, "").0,
            "HTTP/1.1 404 Not Found"
        );
        assert_eq!(
            send(address, "", "GET", Some("evil.example"), "").0,
            "HTTP/1.1 400 Bad Request",
            "a name that points here by a trick of the network is not served"
        );
        assert_eq!(
            send(address, "nothing", "GET", None, "").0,
            "HTTP/1.1 404 Not Found"
        );
        let port = address
            .split(':')
            .nth(2)
            .unwrap()
            .split('/')
            .next()
            .unwrap()
            .to_string();
        assert_eq!(
            send(address, "", "GET", Some(&format!("localhost:{port}")), "").0,
            "HTTP/1.1 200 OK"
        );
    }

    #[test]
    fn should_refuse_an_address_that_is_not_allowed_and_ask_nothing_of_anybody() {
        let served = serve("refuse");

        let (status, text) = send(
            &served.page.address,
            "link",
            "POST",
            None,
            &form_for("http://mnemarr.lan:8384"),
        );
        assert_eq!(status, "HTTP/1.1 200 OK");
        assert!(text.contains("must be https://"), "{text}");
        let (_, empty) = send(&served.page.address, "link", "POST", None, "server=");
        assert!(empty.contains("Type the address"));

        assert!(served.opened.lock().unwrap().is_empty());
    }

    #[test]
    fn should_ask_open_the_page_of_mnemarr_wait_and_keep_the_key_when_a_person_approves() {
        let (mnemarr, handle) = serve_in_turn(vec![
            (
                "201 Created",
                STARTED.replace("https://m.example", "{server}"),
            ),
            ("200 OK", r#"{"state":"pending"}"#.to_string()),
            (
                "200 OK",
                r#"{"state":"approved","key":"mnm_the-key","machine":"gaming-pc","kind":"intermittent"}"#.to_string(),
            ),
        ]);
        let served = serve("link");

        let (status, _) = send(
            &served.page.address,
            "link",
            "POST",
            None,
            &format!("{}&insecure=1", form_for(&mnemarr)),
        );
        assert_eq!(status, "HTTP/1.1 303 See Other");
        let mut text = String::new();
        for _ in 0..200 {
            text = send(&served.page.address, "status", "GET", None, "").1;
            if text.contains("Linked to") {
                break;
            }
            thread::sleep(Duration::from_millis(25));
        }

        assert!(
            text.contains("Linked to") && text.contains("gaming-pc"),
            "{text}"
        );
        assert_eq!(handle.join().unwrap(), 3);
        assert_eq!(
            *served.opened.lock().unwrap(),
            vec![format!("{mnemarr}/link?code=ABCD-EFGH-JKLM")],
            "an address on the Mnemarr that was typed is opened"
        );
        let linked = destinations::load(&served.dir);
        assert_eq!(linked.len(), 1);
        assert_eq!(
            (linked[0].machine.as_str(), linked[0].insecure),
            ("gaming-pc", true)
        );
        assert_eq!(
            destinations::key_of(&served.dir, &linked[0]).as_deref(),
            Some("mnm_the-key")
        );
    }

    #[test]
    fn should_show_the_code_while_it_waits_and_say_what_the_server_said_as_plain_text() {
        let (mnemarr, handle) = serve_in_turn(vec![
            ("201 Created", STARTED.to_string()),
            (
                "400 Bad Request",
                r#"{"title":"<b>no</b> thanks"}"#.to_string(),
            ),
        ]);
        let served = serve("failed");
        send(
            &served.page.address,
            "link",
            "POST",
            None,
            &format!("{}&insecure=1", form_for(&mnemarr)),
        );

        let mut text = String::new();
        for _ in 0..200 {
            text = send(&served.page.address, "status", "GET", None, "").1;
            if text.contains("could not be asked") {
                break;
            }
            thread::sleep(Duration::from_millis(25));
        }

        handle.join().unwrap();
        assert!(
            served.opened.lock().unwrap().is_empty(),
            "an address that is not on the Mnemarr that was typed is shown, never opened"
        );
        assert!(text.contains("&lt;b&gt;no&lt;/b&gt; thanks"), "{text}");
        assert!(!text.contains("<b>no</b>"));
        assert!(destinations::load(&served.dir).is_empty());
    }

    #[test]
    fn should_decode_a_form_the_way_a_browser_sends_it() {
        assert_eq!(
            decode("https%3A%2F%2Fm.example+x%2"),
            "https://m.example x%2"
        );
        assert_eq!(
            field("a=1&server=https%3A%2F%2Fm&b=2", "server").as_deref(),
            Some("https://m")
        );
        assert_eq!(field("a=1", "server"), None);
        assert_eq!(
            escape("<a href=\"x\">&'"),
            "&lt;a href=&quot;x&quot;&gt;&amp;&#39;"
        );
    }
}
