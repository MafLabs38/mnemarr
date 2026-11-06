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

/// The side of the icon, in pixels.
const SIZE: u32 = 32;
/// How often the icon looks at how the collector is.
const REFRESH: Duration = Duration::from_millis(500);
/// How long "pause for a while" lasts.
const PAUSE: Duration = Duration::from_secs(3600);

/// The colour of the icon for a condition: green when all is well, grey when paused, amber when Mnemarr cannot be reached, red when something is wrong.
fn colour(condition: &Condition) -> [u8; 3] {
    match condition {
        Condition::Online => [46, 204, 113],
        Condition::Paused => [150, 154, 166],
        Condition::Starting => [120, 150, 210],
        Condition::Offline(_) => [243, 156, 18],
        Condition::Problem(_) | Condition::Refused => [231, 76, 60],
    }
}

/// A round dot of the colour of the condition, with soft edges.
fn icon_for(condition: &Condition) -> Result<Icon, String> {
    let [red, green, blue] = colour(condition);
    let centre = (SIZE as f32 - 1.0) / 2.0;
    let radius = SIZE as f32 / 2.0 - 2.0;
    let mut pixels = Vec::with_capacity((SIZE * SIZE * 4) as usize);
    for y in 0..SIZE {
        for x in 0..SIZE {
            let distance = ((x as f32 - centre).powi(2) + (y as f32 - centre).powi(2)).sqrt();
            let alpha = (radius + 0.5 - distance).clamp(0.0, 1.0);
            // A lighter heart, so that the dot is told from a plain disc in a crowd of icons.
            let heart = if distance < radius * 0.38 { 70 } else { 0 };
            pixels.extend_from_slice(&[
                red.saturating_add(heart),
                green.saturating_add(heart),
                blue.saturating_add(heart),
                (alpha * 255.0) as u8,
            ]);
        }
    }
    Icon::from_rgba(pixels, SIZE, SIZE).map_err(|error| error.to_string())
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
