# Glance: the guide

What every panel shows, how everything works, and where each number comes from. For the short version with pictures, see the [front page](../README.md).

## What it shows

Each block is a section. Profiles decide which sections appear and in what order.

| Section | Readings | Source |
| --- | --- | --- |
| Claude usage | Current session, week (all models), per-model weekly limits such as Fable, each with its reset time and a pace forecast. Where the week's use came from. | Your Claude Code sign-in |
| FPS | Now, frame time, average and 1% low for the app in front | Windows frame events: Direct3D 9 to 12, OpenGL, Vulkan |
| GPU | Load, VRAM, and on NVIDIA cards temperature, power, fan and clock | NVIDIA driver, or Windows counters for AMD and Intel |
| GPU by app | Video memory and load of the five heaviest programs | Windows counters |
| CPU | Load, a bar per thread, clock speed, power, plus temperature if a monitoring tool is running | Windows counters and energy meter, HWiNFO or MSI Afterburner |
| CPU by app | The five programs using the most processor | Windows counters |
| Power | Watts drawn by the CPU and the graphics card, an estimate for the whole PC, and optionally the share of your power supply in use and the cost per hour | Windows energy meter, NVIDIA driver, monitoring tools |
| Memory | RAM in use, free, committed | Windows |
| Memory by app | The five programs using the most memory | Windows counters |
| Disk activity | Active time of the busiest disk, read and write speed | Windows counters |
| I/O by app | The five programs reading and writing the most (files, network and devices together) | Windows counters |
| Drive space | Free space per fixed drive | Windows |
| Network | Download and upload, and how much of each is web (internet) and how much LAN (your local network) | Windows counters and socket events |
| Network by app | The five programs sending and receiving the most, with the speed each way averaged over 5 seconds | Windows socket events |
| Web by app | The same, counting only traffic with the internet | Windows socket events |
| LAN by app | The same, counting only traffic with your local network | Windows socket events |
| Ping | Latency, average, jitter and packet loss to a host you choose | One ping per refresh |
| Extra sensors | Any readings you pick from HWiNFO or MSI Afterburner: fans, voltages, drive temperatures | That tool's shared readings |
| Battery | Charge, power source, time left. Only on PCs that have one. | Windows |
| Weather | Temperature, feels like, wind, humidity, today's range, rain chance | Open-Meteo |
| Benchmark | The timer while recording, then the last result | See Benchmarks below |
| PC specs | CPU, GPUs, memory, display, Windows version | Windows |

A section that cannot work on a PC (Claude usage without Claude Code, Battery on a desktop) is left out rather than showing an error.

## Using it

| Action | Default shortcut | Also |
| --- | --- | --- |
| Show or hide the widget | `Ctrl+Alt+U` | Left-click the icon near the clock |
| Show or hide the compact strip | `Ctrl+Alt+Shift+U` | Right-click menu |
| Start or stop a benchmark | `Ctrl+Alt+Shift+B` | Right-click menu |
| Next profile | `Ctrl+Alt+Shift+P` | Right-click menu, Profile |
| Settings | | Hover over the widget and click the gear, or double-click the icon near the clock |
| Move | | Drag the widget, or pick a corner in Settings |
| Refresh now | | Double-click the widget |

Starting `Glance.exe` while it is already running shows the widget, which makes a desktop shortcut work as a "show" button.

### Panels

Each section is a panel of its own. To rearrange them, drag a panel by the six dots beside its heading: a coloured line shows where it will land. Drop it between two panels, at the top or foot of another column, or past either side of the widget to start a new column. The arrangement is remembered per profile.

Until you drag one, Glance arranges the panels itself into the number of columns chosen in Settings, adding columns if they would not fit the height of the screen. Choosing a number of columns again hands the arranging back to Glance.

Dragging anywhere else on the widget, such as its title line or the gaps between panels, moves the whole widget.

Panels keep their size while the widget is on screen. The lists of busiest programs always have room for five, with blank lines when fewer are busy, and a reading with no value for the moment shows a dash. A panel that has to grow (a message appearing, for example) does so once, and gives the space back when the widget is next hidden.

### Colours

Settings, Widget, Colours switches between nine colour schemes: Midnight, Graphite, Ocean, Forest, Ember, Violet, Light, High contrast and OLED black. A scheme is one line in [Theme.cs](../Theme.cs), so adding your own is easy.

### OLED mode

An OLED pixel dims a little with every hour it is lit, so anything that sits still for long enough (a label, a border, the end of a bar) can leave its outline behind. OLED monitors and TVs guard against this by shifting the whole picture by a pixel now and then. OLED mode, in Settings, Widget, does the same for the widget, which matters if you leave it or the compact strip on screen for hours.

- **Each move is one pixel**, left, right, up or down, so it is not seen happening.
- **How often:** every minute unless you choose every 30 seconds or every 3 minutes. Burn-in builds up over hundreds of hours, so what protects the screen is every position getting an equal share of the time in the long run, not speed. For comparison, Dell says its Alienware OLED monitors move the whole picture by one pixel roughly every three minutes ([Dell's note on pixel shift](https://www.dell.com/support/kbdoc/en-us/000197755/Alienware-34-Curved-Oled-Monitor-Aw3423dw-Pixel-Shift-Pixel-Orbiter)), and MSI's offer slow, normal and fast settings ([MSI on OLED Care](https://www.msi.com/blog/how-msi-oled-care-prevents-oled-from-burn-in)). The widget is small and often on screen briefly, so it goes a little faster.
- **How far:** 8 pixels across and down unless you choose 4 or 12. The widget's thinnest details are one or two pixels wide and its bars a few pixels tall, so that is enough for no pixel to be lit all the time by a stroke or an edge. More would only make the movement visible.
- **Where:** a widget pinned to a corner or edge only strays inwards, so it never slides off screen. One you placed by hand strays evenly around that spot, and dragging it sets a new spot.
- **Evenly:** the path snakes through every position in its box and back, so each gets the same share. Where it is on the path comes from the clock, so hiding and showing the widget, or restarting Glance, carries on rather than starting again from the same spot.

Moving protects fine detail. It cannot protect the middle of a large solid shape, which stays lit wherever the widget goes ([Rtings found the same](https://www.rtings.com/tv/learn/permanent-image-retention-burn-in-lcd-oled) for a large logo on a TV). The colours "OLED black" help there: black is a pixel switched off, which does not wear at all, and the text is kept below full brightness. Neither replaces the monitor's own protection. Leave that switched on.

### Power

The Power section adds up what can be measured: the CPU (from the energy meter built into Windows, or a monitoring tool) and the graphics card (from the NVIDIA driver, or a monitoring tool). Nothing in a desktop PC measures the rest, so "Whole PC" is an estimate: the measured watts, plus an allowance for the motherboard, memory, drives and fans, divided by the power supply's efficiency to give what is drawn at the wall. The allowance (50 W) and efficiency (90%) can be changed in Settings, Data. Give your power supply's size there to see how much of it is in use, and your electricity price to see the cost per hour. A laptop running on battery shows the drain on the battery instead, which is the whole machine, measured.

### Claude usage and its limit

The usage service rate limits callers that ask too often, so Glance never asks more than once every 3 minutes, however often the widget is shown or its settings changed. In between it shows the last answer, and says how old it is once that is over a minute and a half. Restarting Glance does not cost a request either: it carries on from the last saved answer.

To check sooner, press the refresh button on the Claude panel (or "Check Claude usage now" in the right-click menu). If the service does rate limit, Glance waits at least 10 minutes before trying again and shows when that will be.

### Compact strip

One line of the readings you choose, for example `FPS 142  GPU 97% 64 °C  CPU 41%`. Only the sections those readings come from are measured while the strip is showing. Pick the readings and their order in Settings, Tray and strip.

### Tray icon

The icon near the clock can show one live reading as a number, coloured amber or red as it climbs: Claude session percentage, GPU temperature, FPS, whatever you pick in Settings. That reading stays measured while the widget is hidden (Claude usage every few minutes, hardware every couple of seconds).

### Profiles

A profile is a named, ordered set of sections. Glance starts with "Everyday" and "Gaming". Switch with the shortcut or the menu, or have Glance switch by itself while a full-screen app is in front and back again afterwards.

### Graphs and ranges

In Settings, Widget:

- **Graphs** draws the last minute of load, temperature, frame rate, network and disk speed as a small line in place of the bar.
- **Lowest and highest** adds the range seen for each of those readings. Reset it from the right-click menu.

### Claude alerts

A notification when a limit passes 80% and 95% (changeable), and when the session starts over. With alerts on, usage keeps being checked while the widget is hidden, at the interval set in Settings (never under 3 minutes).

## Benchmarks

Press the benchmark shortcut to start recording and again to stop. A red REC timer shows on the widget and strip. The widget can be hidden while it records.

Glance records every frame time of the app in front, and once a second every hardware reading. When stopped it writes three files to `Documents\Glance benchmarks`:

- `<date> <app>.txt`: the summary. Average, 1% low and 0.1% low frame rate, and the lowest, average and highest of every reading.
- `<date> <app>.csv`: the readings, one line a second.
- `<date> <app> frames.csv`: every frame time in milliseconds.

"1% low" is the frame rate of the slowest 1% of frames: their average frame time turned back into frames per second.

## Settings

Everything in the settings window applies immediately. The tabs are Widget (colours, position, columns, size, opacity, graphs, OLED mode), Shortcuts (the four shortcuts and starting with Windows), Sections (profiles, which sections, their order), Tray and strip, Data (update rates, monitoring tools, extra sensors, power figures, ping host, weather town), and About (the version, who made it, and a check for updates).

Settings are kept in `%APPDATA%\Glance\config.json`. Two options only exist there: `claude.show` (which limits to list, in order, with `*` wildcards, for example `["session", "weekly_fable"]`) and `claude.labels` (rename a limit, for example `{ "weekly_all": "This week" }`). Restart Glance after editing the file by hand.

## What needs what

Most sections work for any user on any PC. A few depend on the machine:

- **FPS** needs permission to read Windows performance events: an administrator, or an account in the Performance Log Users group. If the account lacks it, the FPS section says so and Settings offers a button that adds the account to that group (Windows asks for approval, then sign out and back in).
- **Web and LAN split, and the Network, Web and LAN by app panels** need the same permission as FPS, and nothing more: they do not need Glance to be run as administrator. Without the permission the Network section shows total download and upload. Three things to know about these figures:
  - They count what programs send and receive through their own connections. Traffic that Windows carries itself, of which file sharing between PCs is the main example, appears in the totals but not in the split or against a program.
  - A connection that was already open when the section started does not say where it leads, so its traffic is put down as web or LAN according to where most of that program's connections go.
  - Traffic between programs on this PC itself is left out.
- **Starting as administrator** was an option in versions 0.3 and 0.4, for the web and LAN split. It is no longer needed. If you switched it on, Settings, Shortcuts shows a button that puts Glance back to a normal start.
- **CPU power** comes from the energy meter Windows provides for most recent Intel and AMD processors. Where there is none, it comes from a monitoring tool, as below.
- **GPU temperature, power, fan and clock** come from the NVIDIA driver. On AMD and Intel graphics the section shows load and VRAM.
- **CPU temperature** cannot be read by Windows without a hardware driver, so Glance borrows it from a monitoring tool if one is running:
  - HWiNFO, with Shared Memory Support switched on in its settings.
  - MSI Afterburner, with CPU temperature (and CPU power, if Windows has no energy meter for your processor) ticked on its Monitoring tab.
  - CPU-Z is not supported: it has no interface other programs can read.
- **The overlay itself** cannot draw over a game in exclusive full screen. Borderless or windowed full screen works.

## Privacy

- **Claude usage:** Glance reads the access token Claude Code keeps in `%USERPROFILE%\.claude\.credentials.json` and asks `https://api.anthropic.com/api/oauth/usage` for the figures that `/usage` shows. The token is read fresh for each request, sent only to api.anthropic.com, and never stored, logged or renewed by Glance. If it has expired the widget says so, and any Claude Code activity renews it. That endpoint is not documented by Anthropic and could change. The last response is saved to `%APPDATA%\Glance\last-usage.json` (usage numbers only) for troubleshooting.
- **Weather:** nothing is sent until you pick a town. After that, only that town's coordinates, rounded to about a kilometre, go to open-meteo.com.
- **Updates:** the Check for updates button on the About tab asks GitHub for the newest release of Glance. It only runs when pressed, sends nothing about you or your PC, and downloads nothing: if there is a newer version it offers to open the download page.
- **Ping:** off until you add the section. It then pings the host you chose while the section is showing.
- **Everything else** is read locally and goes nowhere.

Errors are written to `%APPDATA%\Glance\glance.log`.
