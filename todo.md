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
- [ ] Copy `C:\eStreamAutomation` to the dev PC and fix the TODOs in the code:
  stop words, UPC edge cases, results CSV status strings, Force and the 50% rule, the new sheet number.
- [ ] Dry run on 60410079591 (LAYS ALL DRESS 220 g) and 60410079966 (CUCMBER LAYS) passes.
- [ ] One real SAVE, with the owner watching: Applied and verified.
- [ ] Stock sheet: Check, then Make stock sheet with a few counts.
- [ ] First real stock sheet POST: only with the owner's real counts and go-ahead.
- [ ] A script timeout is handled (rows show Failed, app keeps working).
- [ ] Engine calls show in the app log with exit code and time.

## Still to build after testing
- [ ] Stop after current item and "row x of N" (needs progress output from the scripts).
- [ ] Power Automate Desktop executor (optional; needs a non-interactive flow).
- [ ] Lookups through `Get-EsItem.ps1` when SQL is down.
