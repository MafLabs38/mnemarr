//! The icon in the notification area (ADR 0027 §16): a person sees that the collector runs, how it is, and can pause it, resume it, open Mnemarr or stop
//! it, without a window and without a command. Windows only for now. The collector keeps collecting in its own thread; this one only draws and listens.

use std::convert::Infallible;
use std::path::PathBuf;
use std::process::Command;
use std::time::{Duration, Instant, SystemTime};

use tao::event_loop::{ControlFlow, EventLoop};
use tray_icon::menu::{Menu, MenuEvent, MenuItem, PredefinedMenuItem};
use tray_icon::{Icon, TrayIconBuilder};

use crate::control;
use crate::status::{self, Condition, Shared};

/// The side of the icon, in pixels. Windows draws it smaller, from this.
const SIZE: u32 = 64;
/// How often the icon looks at how the collector is.
const REFRESH: Duration = Duration::from_millis(500);
/// How long "pause for a while" lasts.
const PAUSE: Duration = Duration::from_secs(3600);

// The brain of Mnemarr, as a thin line on a transparent ground; its colour says how the collector is. Drawn from `assets/brain.svg` by `tools/icons/render.mjs`.
const ONLINE: &[u8] = include_bytes!("../assets/brain-online.rgba");
const PAUSED: &[u8] = include_bytes!("../assets/brain-paused.rgba");
const OFFLINE: &[u8] = include_bytes!("../assets/brain-offline.rgba");
const STARTING: &[u8] = include_bytes!("../assets/brain-starting.rgba");

/// The icon for a condition: a green brain when all is well, orange when paused, red when Mnemarr cannot be reached or something is wrong, grey-blue while it starts.
fn icon_for(condition: &Condition) -> Result<Icon, String> {
    let pixels = match condition {
        Condition::Online => ONLINE,
        Condition::Paused => PAUSED,
        Condition::Starting => STARTING,
        Condition::Offline(_) | Condition::Problem(_) | Condition::Refused => OFFLINE,
    };
    Icon::from_rgba(pixels.to_vec(), SIZE, SIZE).map_err(|error| error.to_string())
}

/// Opens an address in the browser of the person, without a console window.
fn open_in_browser(address: &str) {
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    let _ = Command::new("cmd")
        .args(["/C", "start", "", address])
        .creation_flags(CREATE_NO_WINDOW)
        .spawn();
}

/// Shows the icon and listens to its menu. Does not return while the icon is there: *Quit* ends the program. It returns an error, before it shows
/// anything, when the icon cannot be made (no taskbar, a session with no desktop): the collector then simply goes on without it.
pub fn run(
    dir: PathBuf,
    server: String,
    machine: String,
    condition: Shared,
) -> Result<Infallible, String> {
    let title = MenuItem::new(format!("Mnemarr collector: {machine}"), false, None);
    let state_line = MenuItem::new("starting", false, None);
    let pause_hour = MenuItem::new("Pause for one hour", true, None);
    let pause_always = MenuItem::new("Pause until I resume", true, None);
    let resume = MenuItem::new("Resume", false, None);
    let open = MenuItem::new("Open Mnemarr", true, None);
    let quit = MenuItem::new("Quit the collector", true, None);
    let menu = Menu::new();
    let separator_one = PredefinedMenuItem::separator();
    let separator_two = PredefinedMenuItem::separator();
    for result in [
        menu.append(&title),
        menu.append(&state_line),
        menu.append(&separator_one),
        menu.append(&pause_hour),
        menu.append(&pause_always),
        menu.append(&resume),
        menu.append(&separator_two),
        menu.append(&open),
        menu.append(&quit),
    ] {
        result.map_err(|error| error.to_string())?;
    }

    let tray = TrayIconBuilder::new()
        .with_menu(Box::new(menu))
        .with_tooltip(format!("Mnemarr collector: {machine}"))
        .with_icon(icon_for(&Condition::Starting)?)
        .build()
        .map_err(|error| error.to_string())?;

    let address = format!("{}/machines", server.trim_end_matches('/'));
    let mut shown = Condition::Starting;
    EventLoop::new().run(move |_event, _target, control_flow| {
        *control_flow = ControlFlow::WaitUntil(Instant::now() + REFRESH);
        while let Ok(event) = MenuEvent::receiver().try_recv() {
            if event.id == *pause_hour.id() {
                let _ = control::pause_for(&dir, PAUSE, SystemTime::now());
            } else if event.id == *pause_always.id() {
                let _ = control::pause_forever(&dir);
            } else if event.id == *resume.id() {
                let _ = control::resume(&dir);
            } else if event.id == *open.id() {
                open_in_browser(&address);
            } else if event.id == *quit.id() {
                *control_flow = ControlFlow::Exit;
            }
        }
        // A pause is what the person asked for, and shows at once; the rest is what the collector last said of itself.
        let paused = control::current(&dir, SystemTime::now()).is_some();
        let now = if paused {
            Condition::Paused
        } else {
            status::get(&condition)
        };
        if now != shown {
            if let Ok(icon) = icon_for(&now) {
                let _ = tray.set_icon(Some(icon));
            }
            let _ = tray.set_tooltip(Some(format!(
                "Mnemarr collector: {machine}, {}",
                now.label()
            )));
            state_line.set_text(now.label());
            resume.set_enabled(paused);
            pause_hour.set_enabled(!paused);
            pause_always.set_enabled(!paused);
            shown = now;
        }
    })
}
