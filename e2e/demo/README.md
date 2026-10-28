# Demo: one claim, two companies, two policies

A narrated, headed Playwright walkthrough of about 5 minutes, run against the app you already have running. The same accidental screen crack on a tablet bought 10 months ago is claimed at **Aurora Electronics** and at **Borealis Devices**:

| Company | Product | Policy says | Outcome |
|---|---|---|---|
| Aurora Electronics | `AUR-TAB10` | accidental damage excluded (`AUR-WP-3.1`) | **Rejected** automatically |
| Borealis Devices | `BOR-SLATE11` | one accidental-damage incident in 12 months covered (`BOR-WP-1.2`) | **Approved** automatically, repair |

The walkthrough has four parts:

1. **Aurora customer:** fills in the claim on Aurora's portal and gets the outcome with a plain-language explanation.
2. **Borealis customer:** files the same claim on Borealis' portal and gets the outcome.
3. **Aurora back office**, as `reviewer.aurora`, follows how the AI decided:
   - The claims list shows only Aurora's claims.
   - The case file shows the status, "Decided by system", and the intake agent's reading of the description (claimed cause: accidental drop).
   - The evidence agent's invoice extraction, with its cross-check against the claim.
   - The evidence agent's photo analysis (cracked screen, consistent with the description).
   - The recommendation, confidence and risk.
   - The reasoning and every guardrail check.
   - The cited clause next to the clauses that were retrieved but not cited (this company's policy only).
   - The automatic action.
   - The decision trace: hash-chain integrity, and a technical view with tenant-scoped retrieval and every model call's model, tokens and cost.
4. **Borealis back office**, as `reviewer.borealis`: the same steps, then the remedy (a repair, decided by policy) and the suggested repair centre.

Throughout the demo, a **"How the decision is made"** tracker sits in the free space at the left. It shows:

- the company;
- the one policy rule that decides this case;
- every stage: customer → intake, evidence, policy and decision agents → guardrails → automatic decision → audit trail (→ remedy);
- the stage on screen, highlighted, and the outcome in red or green.

While the AI processes a claim, the highlight moves through the AI stages.

The claims are golden scenarios `S4-aurora` and `S4-borealis`, so in replay mode the AI answers come from the recordings. You need no API key and nothing is billed.

## Run it

1. Start the app in replay mode (repo root):

   ```powershell
   $env:DOTNET_ASPIRE_CONTAINER_RUNTIME = "podman"; $env:AiGateway__Mode = "replay"
   dotnet run --project src/Warranty.AppHost      # or: aspire run
   ```

2. Run the demo. It uses Microsoft Edge, which ships with Windows, so there's nothing to download. The window switches to full screen. Press `F11` only if you need to leave full screen.

   ```powershell
   npm --prefix e2e/demo ci                         # first time only
   npx --prefix e2e/demo playwright install ffmpeg  # first time only: for the video recording
   npm --prefix e2e/demo run demo
   ```

Options (environment variables):

- `DEMO_PACE`: multiplies every pause. The default of 0.9 takes about 5 minutes; `0.2` is a 1½-minute rehearsal; raise it if you talk over the captions.
- `DEMO_HEADLESS=1`: run without a visible browser.
- `DEMO_SNAPSHOTS=1`: save a screenshot of every highlighted step to `test-results/snapshots/`.
- `DEMO_BROWSER_CHANNEL=chrome`: use another browser.

Every run is recorded to `e2e/demo/test-results/**/video.webm`. Keep a good recording as a backup for the live demo.

## Reset between runs

The platform flags a second claim on the same serial as a possible duplicate (`DUPLICATE_SERIAL_CLAIM`) and sends it to human review. So each run needs a fresh database.

The demo checks this before it starts. If an earlier run's claims are still there, it stops within seconds and tells you to reset. You can reset in one of two ways:

```powershell
npm --prefix e2e/demo run reset        # reset the database and restart the app (~3 minutes)
npm --prefix e2e/demo run demo:fresh   # reset, then run the demo
```

`reset` needs the app to be running. It does the following:

1. Finds the app's Postgres volume.
2. Stops the AppHost.
3. Deletes the volume, which removes every claim in the local database.
4. Starts the AppHost again in the background, in replay mode with Podman.
5. Returns when the web app answers.

The app's log goes to `e2e/demo/test-results/apphost.log`. To stop the app later, run `Stop-Process -Name Warranty.AppHost`.

Best practice: rehearse, then run `reset`, then do the live demo.
