# To test

Everything below is built but hasn't been tried for real yet. Most of it needs the store PC.

## With a real API key
- [ ] Settings → Test connection works.
- [ ] A chat question gets an answer, and the second message shows cache reads in the app log (not 0).
- [ ] "lays cucumber" gives numbered choices; clicking one works.
- [ ] Receipt: drop a real invoice photo. Lines, costs and unreadable lines look right.
- [ ] Receipt: "30% markup on the chips" gives sensible prices.
- [ ] Receipt: try a PDF invoice.
-  ] Count sheet photo: counts are read and only added after you confirm.
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
- [ ] The installer runs without admin rights; the app starts from the Start menu.
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
- [ ] Copy the hand-off's `eStreamUI.psm1` and every `.ps1` to `C:\eStreamAutomation` on the store PC if they aren't there yet
  (the hand-off says every script imports the new module).
- [ ] Delete any old `assistant_*.csv` left in `C:\eStreamAutomation\Inbox` by earlier versions (they would be applied again
  by a plain `Invoke-EsCsv.ps1` run). The app now keeps its CSVs in its own runs folder.
- [ ] Dry run on 60410079591 (LAYS ALL DRESS 220 g) and 60410079966 (CUCMBER LAYS) passes.
- [ ] One real SAVE, with the owner watching: Applied and verified.
- [ ] Stock sheet: Check, then Make stock sheet with a few counts.
- [ ] First real stock sheet POST: only with the owner's real counts and go-ahead.
- [ ] A script timeout is handled (rows show Failed, app keeps working).
- [ ] Engine calls show in the app log with exit code and time.

## Phase C features (on the store PC, TEST items 2999990000011 / 2999990000028 first)
- [ ] Price batch: stage price changes (prices ending in 0 or 5) for 2+ items (TEST items A and B are enough), dry run: the rows say "Price batch dry run OK",
      and no new batch is left in Inventory > Price Batches.
- [ ] Price batch SAVE (owner watching): the batch is made, then applied (can take ~3 minutes); rows show Applied, and
      Inventory + the till prices match. The app log shows the batch number.
- [ ] A list with one deposit/min/max row and three price rows: the one row goes through the item editor, the rest through the batch.
- [ ] A price like $4.99 stays on the item editor (the batch would round it to $5.00).
- [ ] Receipt → *Add stock (stock sheet)…*: the confirmation shows "on hand X + received Y = count Z" (negative on hand as 0),
      the sheet is made, the receipt lines show "on stock sheet N", and a second press leaves them out.
- [ ] Post that sheet from the Stock counts tab (owner's real receipt only): on hand goes up by exactly the received units.
- [ ] Receipt pack sizes: a case line gives qty × packs × units; check a few real invoices against eStream's case sizes.
- [ ] Reports → Shelf labels to print → *Queue in eStream…*: the labels show in Labels Print under the chosen design,
      the owner's other queued labels are untouched, and queued items leave the app's list.
- [ ] A "2 for" label: check how it prints on GROCERY SHELVES (Phase C saw "2 For $" with no amount).
- [ ] Claude: "add the stock from this receipt" uses receipt_stock_sheet; "queue the labels" uses queue_shelf_labels; both ask first.

## Still to build after testing
- [ ] Stop after current item and "row x of N" (needs progress output from the scripts).
- [ ] Publish-EsStockSheet.ps1: give it a RESULT line and exit codes like the other scripts (hand-off suggestion).
- [ ] Import-EsPriceBatch.ps1: a clearer per-line status than "DryRunOK" for lines in a real pending batch (the app copes as it is).
- [ ] Power Automate Desktop executor (optional; needs a non-interactive flow).
- [ ] Lookups through `Get-EsItem.ps1` when SQL is down.
