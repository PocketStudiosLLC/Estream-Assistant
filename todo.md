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

## v0.1.7: tested in test 3 (2026-11-23)
Passed: "N for" rule, 4+ batch routing, zero-count sheet refusal, PDF receipts, Receipts folder, file picker, stock
reports (match SQL, active items), stock_report tool, Export CSV, Test connection, choice buttons, prompt cache, PAD button.
Found and fixed in v0.1.8: GM per pack wrong for the store's 3496 "1 × N" items, margin report SQL overflow, SAVE
timeout too short when eStream Server is busy, a half-made stock sheet shown as fine, Claude not knowing lists were
cleared, receipt codes that are shelf codes (Costco).

## v0.1.8: tested in test 4 (2026-11-23), all passed
GM on "1 × N" items, margin reports, Claude and the lists, SAVE timings (~18 s an item), test 3 leftovers (SAVE notes,
receipt guard, POST with the fixed Publish script's RESULT line, Invoke-EsCsv moving Inbox files, save refused, real-item
dry run), Costco shelf-code matching, negative on-hand warning, Reports → Stock sheets with protected marks.
Found and fixed in v0.1.9: numbered facts turned into buttons / lost, deleted sheet still listed, settings file not
rewritten until Save, GM note on 1 × 1 items, double full stop, 12-digit barcode warning.

## v0.1.9: run `docs/test5.txt` on the store PC
- [ ] Facts lists stay as text, choices still buttons ("lays cucumber").
- [ ] Sheet 10763 shows "deleted in eStream"; card GM note; 12-digit warning in the Costco stock sheet window.
- [ ] Owner: pack sizes (Coke 1 × 32, cigarettes 1 × 10, concrete 1 × 56), count sheet chat question, sheet 10761,
      TEST labels ("2 for" price on the label?), HAAGEN DAZS 5 for $9.35, CUCMBER LAYS in pending batches.
- [ ] Owner hands-on: scanner, drag and drop, phone photos, print, scaling, a real invoice.
- [ ] 12-digit barcodes on stock sheets: does eStream's import find them as 11 digits (drop the check digit)? 12,046
      items have one. Needs a test item with a 12-digit barcode, or the owner's OK on one real item.

## Still to build after testing
- [ ] Stop after current item and "row x of N" (needs progress output from the scripts).
- [x] Publish-EsStockSheet.ps1 RESULT line + exit 1, Invoke-EsCsv.ps1 moving Inbox files (done on the store PC in
      test 3, Handoff-Test3\FIX-NOTES.txt; on-screen run in test 4).
- [ ] Zero stock report: capped at 5000 rows sorted by sold, so the top is odd items. Maybe sort or filter better.
- [ ] Import-EsPriceBatch.ps1: a clearer per-line status than "DryRunOK" for lines in a real pending batch (the app copes as it is).
- [ ] Power Automate Desktop executor (optional; needs a non-interactive flow).
- [ ] Lookups through `Get-EsItem.ps1` when SQL is down.
