# MassWithdraw

**MassWithdraw** is a lightweight Dalamud plugin for **Final Fantasy XIV** that automates withdrawing all items from a retainer’s inventory directly into the player inventory — safely, asynchronously, and with respect for unique items.

---

## ✨ Features

- One-click **mass withdraw** from the active retainer’s inventory
- **Asynchronous transfers** with per-item delay (400 ms)
- **Pre-check preview**: shows how many stacks can be moved, free bag slots, and estimated time
- **Unique item protection** — automatically skips items that cannot be duplicated
- **Safe cancel option** during transfer

---

## 🕹️ Usage

1. Log in and open a **Retainer Bell**.  
2. Select a retainer and open their **Inventory**.  
3. Use the command `/masswithdraw`.  
4. Click **Begin Withdraw** to transfer all available items.  
5. Click **Stop Transfer** at any time to cancel safely.

The window cannot be closed with **Esc**, to prevent accidental interruption during transfers.

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

## ❤️ Credits

- Dalamud API — [goatcorp/Dalamud](https://github.com/goatcorp/Dalamud)  
- FFXIVClientStructs — [aers/FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs)  
