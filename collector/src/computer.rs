//! The identity of the computer (ADR 0031): a stable identifier that survives removing the collector and installing it again, so that a Mnemarr knows
//! the computer it already has under another name. It is the identifier the system made for itself at its own installation (`MachineGuid` on Windows,
//! `/etc/machine-id` on Linux, the platform UUID on macOS), and it is sent only as a SHA-256 hash of that text with a fixed prefix, never in clear.

use std::sync::OnceLock;

const PREFIX: &str = "mnemarr-computer:";

/// The hash that identifies this computer, 64 hexadecimal digits, or `None` when the system does not say (nothing is then sent: the name is the identity).
pub fn id() -> Option<&'static str> {
    static ID: OnceLock<Option<String>> = OnceLock::new();
    ID.get_or_init(|| system_identifier().and_then(|raw| hash_of(&raw)))
        .as_deref()
}

/// The system and the processor this program is for, named as a Mnemarr names its programs (`windows-x86_64`, `linux-x86_64`, `macos-aarch64`).
pub fn platform() -> String {
    format!("{}-{}", std::env::consts::OS, std::env::consts::ARCH)
}

/// The SHA-256 of the program that is running, read once: what a Mnemarr compares with the program it carries to tell whether this one is the same (ADR 0031 §7).
/// `None` when the program cannot read itself, and nothing is then said.
pub fn program_hash() -> Option<&'static str> {
    static HASH: OnceLock<Option<String>> = OnceLock::new();
    HASH.get_or_init(|| {
        let path = std::env::current_exe().ok()?;
        std::fs::read(path).ok().map(|bytes| sha256_hex(&bytes))
    })
    .as_deref()
}

/// The hash of an identifier the system gave: its lower-case, trimmed text under a fixed prefix. Nothing for an empty text or an obvious placeholder.
pub fn hash_of(raw: &str) -> Option<String> {
    let trimmed = raw.trim().to_lowercase();
    if trimmed.len() < 8 || trimmed.chars().all(|c| matches!(c, '0' | '-')) {
        return None;
    }
    Some(sha256_hex(format!("{PREFIX}{trimmed}").as_bytes()))
}

#[cfg(windows)]
fn system_identifier() -> Option<String> {
    use std::os::windows::process::CommandExt;
    use std::process::Command;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    let output = Command::new("reg")
        .args([
            "query",
            r"HKLM\SOFTWARE\Microsoft\Cryptography",
            "/v",
            "MachineGuid",
        ])
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .ok()?;
    machine_guid_from(&String::from_utf8_lossy(&output.stdout))
}

#[cfg(target_os = "macos")]
fn system_identifier() -> Option<String> {
    use std::process::Command;
    let output = Command::new("ioreg")
        .args(["-rd1", "-c", "IOPlatformExpertDevice"])
        .output()
        .ok()?;
    platform_uuid_from(&String::from_utf8_lossy(&output.stdout))
}

#[cfg(all(not(windows), not(target_os = "macos")))]
fn system_identifier() -> Option<String> {
    ["/etc/machine-id", "/var/lib/dbus/machine-id"]
        .iter()
        .find_map(|path| std::fs::read_to_string(path).ok())
        .filter(|text| !text.trim().is_empty())
}

/// The value of `MachineGuid` in what `reg query` prints (`    MachineGuid    REG_SZ    <guid>`).
#[cfg_attr(not(windows), allow(dead_code))]
fn machine_guid_from(output: &str) -> Option<String> {
    output
        .lines()
        .find(|line| line.contains("MachineGuid"))
        .and_then(|line| line.split_whitespace().last())
        .filter(|value| *value != "MachineGuid" && *value != "REG_SZ")
        .map(str::to_string)
}

/// The value of `IOPlatformUUID` in what `ioreg` prints (`"IOPlatformUUID" = "<uuid>"`).
#[cfg_attr(not(target_os = "macos"), allow(dead_code))]
fn platform_uuid_from(output: &str) -> Option<String> {
    output
        .lines()
        .find(|line| line.contains("IOPlatformUUID"))
        .and_then(|line| line.split('"').nth(3))
        .map(str::to_string)
}

const ROUNDS: [u32; 64] = [
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
];

/// SHA-256 of `data` as 64 lower-case hexadecimal digits. Written here and not taken from a crate: the collector adds no dependency for one hash of one short text.
pub fn sha256_hex(data: &[u8]) -> String {
    let mut h: [u32; 8] = [
        0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab,
        0x5be0cd19,
    ];
    let mut message = data.to_vec();
    message.push(0x80);
    while message.len() % 64 != 56 {
        message.push(0);
    }
    message.extend_from_slice(&((data.len() as u64) * 8).to_be_bytes());
    for block in message.as_chunks::<64>().0 {
        let mut w = [0u32; 64];
        for (slot, word) in w.iter_mut().zip(block.as_chunks::<4>().0) {
            *slot = u32::from_be_bytes(*word);
        }
        for i in 16..64 {
            let s0 = w[i - 15].rotate_right(7) ^ w[i - 15].rotate_right(18) ^ (w[i - 15] >> 3);
            let s1 = w[i - 2].rotate_right(17) ^ w[i - 2].rotate_right(19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16]
                .wrapping_add(s0)
                .wrapping_add(w[i - 7])
                .wrapping_add(s1);
        }
        let [mut a, mut b, mut c, mut d, mut e, mut f, mut g, mut hh] = h;
        for i in 0..64 {
            let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
            let ch = (e & f) ^ (!e & g);
            let t1 = hh
                .wrapping_add(s1)
                .wrapping_add(ch)
                .wrapping_add(ROUNDS[i])
                .wrapping_add(w[i]);
            let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
            let maj = (a & b) ^ (a & c) ^ (b & c);
            let t2 = s0.wrapping_add(maj);
            hh = g;
            g = f;
            f = e;
            e = d.wrapping_add(t1);
            d = c;
            c = b;
            b = a;
            a = t1.wrapping_add(t2);
        }
        for (slot, value) in h.iter_mut().zip([a, b, c, d, e, f, g, hh]) {
            *slot = slot.wrapping_add(value);
        }
    }
    h.iter().map(|word| format!("{word:08x}")).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn should_hash_the_known_test_vectors() {
        assert_eq!(
            sha256_hex(b""),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
        assert_eq!(
            sha256_hex(b"abc"),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        // Longer than one block, and across the padding boundary.
        assert_eq!(
            sha256_hex(b"abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"),
            "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1"
        );
    }

    #[test]
    fn should_give_the_same_hash_for_the_same_computer_whatever_the_case_or_the_spaces() {
        let one = hash_of("  4C4C4544-0042-5710-8052-C4C04F4B4E32\r\n").unwrap();
        let two = hash_of("4c4c4544-0042-5710-8052-c4c04f4b4e32").unwrap();
        assert_eq!(one, two);
        assert_eq!(one.len(), 64);
        assert!(
            one.chars()
                .all(|c| c.is_ascii_hexdigit() && !c.is_uppercase())
        );
        assert_ne!(
            one,
            hash_of("4c4c4544-0042-5710-8052-c4c04f4b4e33").unwrap()
        );
    }

    #[test]
    fn should_not_send_a_hash_of_nothing_or_of_a_placeholder() {
        assert_eq!(hash_of(""), None);
        assert_eq!(hash_of("   \n"), None);
        assert_eq!(hash_of("00000000-0000-0000-0000-000000000000"), None);
    }

    #[test]
    fn should_read_the_machine_guid_from_what_reg_prints() {
        let printed = "\r\nHKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Cryptography\r\n    MachineGuid    REG_SZ    4c4c4544-0042-5710-8052-c4c04f4b4e32\r\n\r\n";
        assert_eq!(
            machine_guid_from(printed).as_deref(),
            Some("4c4c4544-0042-5710-8052-c4c04f4b4e32")
        );
        assert_eq!(
            machine_guid_from("ERROR: The system was unable to find"),
            None
        );
    }

    #[test]
    fn should_read_the_platform_uuid_from_what_ioreg_prints() {
        let printed =
            "  | {\n  |   \"IOPlatformUUID\" = \"8F3A1B2C-1111-2222-3333-444455556666\"\n  | }";
        assert_eq!(
            platform_uuid_from(printed).as_deref(),
            Some("8F3A1B2C-1111-2222-3333-444455556666")
        );
    }

    #[test]
    fn should_name_the_platform_as_a_mnemarr_names_its_programs_and_hash_its_own_program() {
        let platform = platform();
        assert!(
            [
                "windows-x86_64",
                "linux-x86_64",
                "macos-aarch64",
                "linux-aarch64",
                "macos-x86_64"
            ]
            .contains(&platform.as_str()),
            "{platform}"
        );
        // The program that runs the tests is a file that can be read: its hash is 64 digits, and the same each time.
        let hash = program_hash().expect("the test program can read itself");
        assert_eq!(hash.len(), 64);
        assert_eq!(program_hash(), Some(hash));
    }

    #[test]
    fn should_know_this_computer_here() {
        // On a build machine and in CI the system has an identifier; when it has none the answer is None and nothing is sent.
        if let Some(id) = id() {
            assert_eq!(id.len(), 64);
        }
    }
}
