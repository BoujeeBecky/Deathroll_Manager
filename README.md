# Deathroll Manager

<p align="center">
  <img src="https://tometools.com/shared/icons/DeathrollManagerIcon.png" width="128" alt="Deathroll Manager Icon"/>
</p>

<p align="center">
  A Dalamud plugin for tracking and running Deathroll events at FFXIV venues.
</p>

<p align="center">
  <a href="https://github.com/BoujeeBecky/Deathroll_Manager/releases/latest"><img src="https://img.shields.io/github/v/release/BoujeeBecky/Deathroll_Manager?style=flat-square&label=release&color=gold" alt="Latest Release"/></a>
  <img src="https://img.shields.io/badge/Dalamud-API%209-blueviolet?style=flat-square" alt="Dalamud API 9"/>
  <img src="https://img.shields.io/badge/FFXIV-Dawntrail-blue?style=flat-square" alt="FFXIV Dawntrail"/>
</p>

---

**Deathroll** — two players agree on a starting number and a bet. They alternate `/random [max]` using the previous result as the new max. Whoever rolls 1 loses and pays the bet.

Deathroll Manager handles the tracking, the brackets, the leaderboards, and the live web viewer so you can focus on running your event.

---

## Features

### 🎲 Live Game Tracking
- Auto-detects `/random` rolls from chat — no manual input needed mid-game
- Real-time danger bar that shifts from green → orange → red as the max drops toward 1
- Turn indicator with a one-click **Roll** button that sends `/random [current max]` automatically when it's your turn
- Game-over flash animation on a roll of 1
- Full roll history with optional timestamps
- All results saved automatically between sessions

### ⚔️ Animated Battle Scene
- Stick-figure duel that reacts to every roll in real time
- Sword-swing animation on each roll, danger-tinted HP bars
- Shatter and fade death animation when someone rolls 1
- Optional pop-out window — great for streaming or dual-monitor setups

### 🏆 Tournament Brackets
- **Single Elimination** and **Double Elimination** (Losers Bracket, Grand Finals, and Grand Finals Reset)
- **V-Bracket** (Finals at centre — great for events) and **Left-to-Right** (classic tree) layouts
- Auto-advances when a deathroll game ends — no button press needed
- Right-click any match to force a winner (forfeit / no-show)
- Left-click a completed match to view the full roll-by-roll history
- CSV import for quick player setup; BYEs filled automatically
- Plain-text bracket export to clipboard

### 📡 Relay System
- **Host:** generate a 6-character code and broadcast your live bracket via `/say`
- **Spectate in-game:** enter the code for a read-only bracket that updates in real time
- **Web viewer:** [tometools.com/bracket?code=XXXXXX](https://tometools.com/bracket) — full live bracket in a browser, no plugin needed
- Compressed sync keeps resyncs to just 3–4 chat messages regardless of bracket size
- Sender validation prevents bracket spoofing

### 📊 Leaderboards
- Overall rankings, By Venue filter, and personal My Stats
- Tracks wins, losses, win rate, and net gil per player
- All stats computed live from history — nothing to configure

---

## Installation

Deathroll Manager is a **custom Dalamud plugin**.

1. Open **XIVLauncher → Settings → Experimental**
2. Under **Custom Plugin Repositories**, add:
```
https://raw.githubusercontent.com/BoujeeBecky/Deathroll_Manager/main/pluginmaster.json
```
3. Click **Save** and search for **Deathroll Manager** in the plugin installer

---

## Commands

| Command | Effect |
|---|---|
| `/dr` or `/deathroll` | Open the main window |
| `/dr tournament` | Open the tournament bracket window |
| `/dr settings` | Open settings |

---

## Web Viewer

Any relayed bracket can be followed live in a browser — no plugin required:

```
https://tometools.com/bracket?code=XXXXXX
```

Share the link with your audience or stream it directly on screen.

---

## Support & Contact

Feature requests and bug reports → [tometools.com/contact](https://tometools.com/contact)

If you'd like to support the project:  
☕ [Ko-Fi](https://ko-fi.com/boujeebecky) &nbsp;·&nbsp; $ [Cash App](https://cash.app/$StormyRoxTips)

---

<p align="center"><sub>Deathroll Manager is an unofficial fan project · not affiliated with or endorsed by Square Enix · FINAL FANTASY XIV © Square Enix Co., Ltd.</sub></p>
