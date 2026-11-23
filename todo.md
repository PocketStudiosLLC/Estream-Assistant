# To test

Everything below is built but hasn't been tried for real yet. Most of it needs the store PC.
`docs/test3.txt` (v0.1.7) covers the store-PC items below: Part A the v0.1.7 fixes, Part B the rest.

## With a real API key
- [ ] Settings → Test connection works.
- [ ] A chat question gets an answer, and the second message shows cache reads in the app log (not 0).
- [ ] "lays cucumber" gives numbered choices; clicking one works.
- [ ] Receipt: drop a real invoice photo. Lines, costs and unreadable lines look right.
- [ ] Receipt: "30% markup on the chips" gives sensible prices.
- [ ] Receipt: try a PDF invoice.
- [ ] Count sheet photo: counts are read and only added after you confirm.
- [ ] Model routing: a text message uses the chat model, a message with a photo/PDF uses the receipt model (check the app log).
- [ ] Is Sonnet good enough for chat, or is Haiku enough? Is the receipt model at medium effort right for receipts?
- [ ] Asking Claude to save is refused.

## Reports and stock sheets (on the store PC, Data source = Sql)
- [ ] Open the app log after starting: it says either "Stock sheet tables: columns as expected" or
      "Stock sheet tables are different…" with the real columns. If different, send that line so the sheet queries can be fixed
      (`SqlItemRepository.StockSheetColumns` and the two sheet queries).
- [ ] Reports tab → Negative stock: the count looks right next to eStream, and it comes back in a few seconds.
- [ ] Below minimum / Above re-order max / Zero stock look sensible.
- [ ] Ask the assistant "which items have negative stock?": it uses stock_report and points to the Reports tab.
- [ ] Stock sheets: the open sheets match eStream (Inventory > Stock Sheets), incl. the owner's 10749, 10750, 10754 marked protected.
- [ ] Open one sheet: item names and UPCs are right (if not, `Adjustments.Invennum` isn't the barcode), counted/system match eStream's
      Print Stock On Hand for that sheet.
- [ ] Export CSV opens in Excel; Print prints a readable table.

## On the store PC (Windows)
- [x] The installer runs without admin rights; the app starts from the Start menu.
- [ ] Data source Sql: lookups and name search work, and they're fast.
- [ ] Scanner: scans are picked up wherever the focus is in the app.
- [ ] Drag and drop, Ctrl+V paste and Add receipt… work.
- [ ] Phone photos come out the right way up.
- [ ] A file put in `C:\eStreamAutomation\Receipts` shows up in the app.
- [ ] Finish receipt moves the file to `Receipts\Done`.
- [ ] Open Power Automate Desktop button works.
- [ ] Layout at 125% and 150% display scaling.

## With the real scripts (PowerShell engine)
- [x] Copy `C:\eStreamAutomation` to the dev PC and fix the TODOs in the code (done from the Phase C hand-off, 2026-11-22).
- [x] Copy the hand-off's `eStreamUI.psm1` and every `.ps1` to `C:\eStreamAutomation` on the store PC if they aren't there yet
  (the hand-off says every script imports the new module).
- [x] Delete any old `assistant_*.csv` left in `C:\eStreamAutomation\Inbox` by earlier versions (they would be applied again
  by a plain `Invoke-EsCsv.ps1` run). The app now keeps its CSVs in its own runs folder.
- [ ] Dry run on 60410079591 (LAYS ALL DRESS 220 g) and 60410079966 (CUCMBER LAYS) passes.
- [x] One real SAVE, with the owner watching: Applied and verified (test 2, TEST items).
- [x] Stock sheet: Check, then Make stock sheet with a few counts.
- [ ] First real stock sheet POST with the owner's real counts (posting itself worked on the TEST items in test 2).
- [ ] A script timeout is handled (rows show Failed, app keeps working).
- [x] Engine calls show in the app log with exit code and time.

## Phase C features: tested on the store PC in test 2 (v0.1.6, 2026-11-23), all passed
Report: `complete/` (gitignored). Price batch import/apply, item editor SAVE, receipt → stock sheet → POST,
label queueing, the large-change stop and Claude asking before receipt_stock_sheet all worked on the TEST items.
Timings: batch import ~16 s + 9 s/line, apply ~22 s; item editor ~14 s check + ~19 s save per item.

## v0.1.7 (fixes from test 2): run `docs/test3.txt` on the store PC
- [ ] GM% in pending rows, receipt lines and the margin report matches eStream (cost per pack = case cost ÷ packs).
- [ ] Receipt "30% margin" prices from the pack cost.
- [ ] "N for" price that doesn't split into cents is skipped with prices that work.
- [ ] 2 price changes go through the item editor (batches only from 4 items); write down the times.
- [ ] Claude describes a cancelled SAVE and a stopped SAVE correctly; both are in the app log.
- [ ] A stock sheet whose counts add up to 0 is refused; a real POST still shows "POSTED OK".
- [ ] No red MOCK warning with Data source = Sql / Dry runs = PowerShell.
- [ ] Owner: preview a "2 for" label (does it show the price?), remove the 4 TEST labels, decide on open sheet 10761.
- [ ] Real items: check a few items' Packs/Case and Units/Pack in eStream. If many real items have units per pack > 1
      for things sold one at a time, the receipt's received units (cases × packs × units) need another look.

## Still to build after testing
- [ ] Stop after current item and "row x of N" (needs progress output from the scripts).
- [ ] Publish-EsStockSheet.ps1: give it a RESULT line and exit 1 on "POSTED BUT CHECK" (test 3 step 6; the app
      already reads "POSTED OK" / "DRY RUN:" and a RESULT line when there is one).
- [ ] Invoke-EsCsv.ps1 -Path leaves the CSV in Inbox (the app isn't affected: it uses its own runs folder).
- [ ] Import-EsPriceBatch.ps1: a clearer per-line status than "DryRunOK" for lines in a real pending batch (the app copes as it is).
- [ ] Power Automate Desktop executor (optional; needs a non-interactive flow).
- [ ] Lookups through `Get-EsItem.ps1` when SQL is down.
