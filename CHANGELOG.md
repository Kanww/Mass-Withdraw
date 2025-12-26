## 🧾 Changelog

- **1.0.1.9** — Fixed performance issues and low FPS during mass withdraw operations.
- **1.0.1.8** — Added filter for submarines part.
- **1.0.1.7** — Changed the filter Any gear to White gear.
- **1.0.1.6** — UI and usability improvements:
  • Added a Config button next to the Transfer button in the main window.
  • Added an option to toggle window anchoring directly from the Retainer Inventory.
- **1.0.1.5** — Refined transfer and preview logic:
  • Removed ETA display.
  • Fixed IsFilterEnabled logic check.
  • Fixed preview not merging items into existing partial stacks.
  • Fixed preview repeatedly rescanning player bags for Unique items.
  • Fixed crash when retainer window was closed mid-transfer.
  • Optimized FindFreeBagSlot() to avoid redundant full scans.
- **1.0.1.4** — Added random delay on each withdraw to avoid actions that look non-human. 
- **1.0.1.3** — Added additional item categories for filtering (All Gear, Materia, Consumables, Crafting Mats)
- **1.0.1.2** — Fixed several UI and command improvements:  
  • Removed flicker when closing the retainer inventory.  
  • Fixed anchor position when resizing the retainer bag.  
  • Added `/masswithdraw transfer` to start transfers directly.  
  • Added `/masswithdraw config` to open the configuration window.  
- **1.0.1.1** — Fixed incorrect messages when the inventory was full or when the retainer bag was empty.  
- **1.0.1.0** — Added filter: Non-white gear.  
- **1.0.0.0** — Initial release.