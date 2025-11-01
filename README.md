# MassWithdraw

**MassWithdraw** is a lightweight Dalamud plugin for **Final Fantasy XIV** that automates withdrawing all items from a retainer’s inventory directly into your player inventory — safely, asynchronously, and with respect for unique items.

---

## ✨ Features

- 📦 One-click **mass withdraw** from the active retainer’s inventory  
- ⚙️ **Asynchronous transfers** with adjustable per-item delay (default 400 ms)  
- 🧭 **Pre-check preview**: shows how many stacks can be moved, free bag slots, and estimated time  
- 🚫 **Unique item protection** — automatically skips items that cannot be duplicated  
- ⏹️ **Safe cancel option** during transfer  
- 🪶 **No lag** or UI freezing (runs in a background thread)  
- 🎨 **Minimal, clean UI** using Dalamud’s ImGui helpers  

---

## 🕹️ Usage

1. Log in and open a **Retainer Bell**.  
2. Select a retainer and open their **Inventory** (Withdraw / Entrust window).  
3. Use the command `/masswithdraw` or open the plugin from the plugin list.  
4. Review the **preview** (retainer stacks, free player slots, ETA).  
5. Click **Begin Withdraw** to transfer all available items.  
6. Click **Stop Transfer** at any time to cancel safely.

💡 The window cannot be closed with **Esc**, to prevent accidental interruption during transfers.

---

## 🧠 Technical Overview

- Written in **C#** for the Dalamud API  
- Uses **FFXIVClientStructs** for direct access to game inventory data  
- Operates asynchronously with a **cancellation token**  
- Respects item uniqueness and player inventory space  
- Default per-move delay: **400 ms** (configurable in source)

---

## 🧩 Command

| Command | Description |
|----------|--------------|
| `/masswithdraw` | Opens the main MassWithdraw window |

---

## 🔧 Configuration

MassWithdraw currently has no configuration window.  
Future updates may include adjustable transfer delay, UI theme options, and detailed logging.

---

## 🧑‍💻 Development

### Requirements
- .NET 9.0 SDK  
- Dalamud development environment  
- FFXIVClientStructs reference

### Build Path
- Solution file: `MassWithdraw.sln`  
- Output DLL: `MassWithdraw/bin/x64/Debug/MassWithdraw.dll`

Load it via Dalamud’s Dev tab → *Local Plugin*.

---

## 🚀 Future Plans & Known Issues

- [ ] Add configurable transfer delay in UI  
- [ ] Stack merge optimization (combine partial stacks before moving)  
- [ ] Option to exclude HQ / collectible items  
- [ ] Optional move summary log  
- [ ] Graceful skip handling for locked slots  

---

## 🧾 License

MIT License © 2025 — *Your Name or Handle*  
See LICENSE for details.

---

## ❤️ Credits

- Dalamud API — [goatcorp/Dalamud](https://github.com/goatcorp/Dalamud)  
- FFXIVClientStructs — [aers/FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs)  
- Concept and implementation — *Your Name or Handle*

---

> “A safe and simple way to clear your retainers without RSI.”
