# To test

Everything below is built but hasn't been tried for real yet. Most of it needs the store PC.

## With a real API key
- [ ] Settings → Test connection works.
- [ ] A chat question gets an answer, and the second message shows cache reads in the app log (not 0).
- [ ] "lays cucumber" gives numbered choices; clicking one works.
- [ ] Receipt: drop a real invoice photo. Lines, costs and unreadable lines look right.
- [ ] Receipt: "30% markup on the chips" gives sensible prices.
- [ ] Receipt: try a PDF invoice.
- [ ] Count sheet photo: counts are read and only added after you confirm.
- [ ] Is effort "low" good enough for receipts, or is "medium" needed?
- [ ] Asking Claude to save is refused.

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
