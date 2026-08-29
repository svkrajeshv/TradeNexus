# NexusApp â€" Automated Index Options Trading Assistant

NexusApp is a Blazor (.NET 10) application that reads trading signals from Telegram
and places / manages Indian index option orders through your broker (Angel One /
AliceBlue). It supports both **paper trading** (practice, no real money) and
**live trading** (real orders).

---

## 1. What the app does (in simple words)

1. You log in to your **broker** (Angel One or AliceBlue) inside the app.
2. You connect your **Telegram** account (one-time OTP).
3. The app **listens** to the Telegram channels you choose.
4. When a signal like *"BUY NIFTY 24000 CE"* arrives, the app:
   - works out the correct **trading symbol** and **nearest expiry**,
   - places the order (paper or live),
   - watches the price and **squares off** automatically when your
     **target** or **stop-loss** is hit.
5. You can see everything live on the **Dashboard**, **Signals**, and **Orders** pages.

---

## 2. Words you should know

| Word | Simple meaning |
|------|----------------|
| **Index option** | A contract on NIFTY, BANKNIFTY, SENSEX, etc. (not a single company share). |
| **CE / PE** | CE = Call (you expect price up). PE = Put (you expect price down). |
| **Strike** | The price level of the option, e.g. 24000. |
| **Expiry** | The last day the option is valid. Weekly or monthly. |
| **CMP / LTP** | Current / Last traded price. |
| **Target** | The profit price where you want to exit. |
| **Stop-loss (SL)** | The loss price where you exit to limit damage. |
| **Square off** | Closing an open position. |
| **Paper trading** | Fake money practice mode. No real orders. |
| **Instrument master** | The broker's daily list of all tradable contracts. |

---

## 3. Which indices are supported

The app trades **only** these index options:

- **NSE (expire on Tuesday):** NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY
- **BSE (expire on Thursday):** SENSEX, BANKEX

**Weekly options** are available only for **NIFTY** and **SENSEX**.
All other indices are **monthly only** (last expiry weekday of the month).
The app always picks the **nearest real listed expiry** from the broker's
instrument master, so weekly signals get weekly expiry and monthly-only
indices get the correct monthly expiry.

---

## 4. First-time setup (step by step)

1. **Open the app** in your browser.
2. Go to the **Accounts** page.
3. Add your broker account (Angel One or AliceBlue) with API key, client id,
   password/PIN and TOTP.
4. Click **Login**.
   - On the **first login each day**, the app cleans and re-downloads the
     instrument master automatically (once per day). Later logins the same day
     are instant.
5. Go to the **Telegram** settings, enter your API id / API hash / phone number,
   and connect. Enter the **OTP** when asked (and 2FA password if you use one).
   - After OTP is verified once, the login **stays saved** â€" you should not need
     to enter OTP again unless the session is cleared.
6. Choose the Telegram **channels** to listen to.
7. Pick **Paper** or **Live** mode.
8. You are ready â€" incoming signals will now be processed.

---

## 5. Everyday use

- **Dashboard** â€" live signals, your watchlist, and open positions.
- **Signals** â€" full history of received signals and how they were parsed.
- **Orders** â€" every order placed, its status and price.
- **Square off** â€" happens automatically on target/SL. You can also square off
  manually from the Dashboard (it uses the live CMP).

**Tip:** Start in **Paper** mode for a few days to confirm signals parse
correctly before switching to **Live**.

---

## 6. Deploying to Azure App Service (B1 plan)

The app runs best on a **B1 (Basic)** Windows App Service plan because B1 gives
you **Always On**, which keeps the Telegram listener connected all day.

> Run the commands one at a time in **PowerShell** and make sure each one
> succeeds before running the next.

### 6.1 Create the Azure resources

```powershell
$rg   = "NexusApp-rg"
$plan = "NexusApp-b1-plan"
$app  = "nexusio"          # must be GLOBALLY unique. If taken, try nexusio-<yourname>

# 1) Resource group
az group create --name $rg --location centralindia

# 2) B1 plan (Windows)
az appservice plan create --name $plan --resource-group $rg --sku B1

# 3) Web app (plain Windows app â€" runtime is bundled by self-contained publish)
az webapp create --name $app --resource-group $rg --plan $plan

# 4) HTTPS only
az webapp update --name $app --resource-group $rg --set httpsOnly=true

# 5) Always On (keeps the Telegram listener alive â€" the reason for B1)
az webapp config set --name $app --resource-group $rg --always-on true

# 6) India Standard Time (aligns the once-per-day master refresh with the trading day)
az webapp config appsettings set --name $app --resource-group $rg --settings WEBSITE_TIME_ZONE="India Standard Time"

# Show the final website address
az webapp show --name $app --resource-group $rg --query defaultHostName -o tsv
```

> **Note on the app name:** it becomes your URL (`https://<app>.azurewebsites.net`)
> and must be unique across all of Azure. If you get *"app names must be globally
> unique"*, pick a more unique name.

### 6.2 Publish the .NET 10 app (self-contained)

Because .NET 10 is new, publish **self-contained** so the runtime ships with the app:

```powershell
cd "D:\Personal\NexusApp - Copy\NexusApp"

dotnet publish .\NexusApp.csproj -c Release -r win-x64 --self-contained true -o .\publish
Compress-Archive -Path .\publish\* -DestinationPath .\app.zip -Force

az webapp deploy --resource-group $rg --name $app --src-path .\app.zip --type zip
```

You can also publish directly from **Visual Studio 2026**:
Right-click the project â†' **Publish** â†' **Azure** â†' **Azure App Service (Windows)**
â†' select your app â†' **Publish**.

---

## 7. Important settings to keep it healthy

- **Always On = On** (Configuration â†' General settings). Without it the Telegram
  listener stops when the site goes idle.
- **Do NOT set `WEBSITE_SKIP_PERSISTENCE`.** Leaving it unset keeps the `%HOME%`
  storage durable so the **Telegram session survives restarts** (no repeated OTP).
- **Single instance only.** Do not scale out â€" the Telegram session cannot be
  shared across multiple instances.
- **`WEBSITE_TIME_ZONE = India Standard Time`** so daily jobs align with IST.

---

## 8. Troubleshooting

| Problem | What to do |
|---------|------------|
| Telegram asks for OTP again and again | Make sure `WEBSITE_SKIP_PERSISTENCE` is **not** set; keep single instance. |
| Signals not arriving | Check broker + Telegram are connected on the Accounts/Telegram pages; check chosen channels. |
| Orders on wrong (monthly) expiry | Log in once to refresh the instrument master (cleans + reloads once/day). |
| "Server Not Found" on Azure URL | The web app was not created yet, or the name is wrong. Re-check step 6.1. |
| Warmup error `RequestUriTooLong` | Point App Service warmup to the lightweight `/health` endpoint. |
| Scale error "No available instances" | Retry later, enable async scaling, or deploy to a new resource group/region. |

---

## 9. Health check

The app exposes a simple health endpoint:

```
GET /health
```

Use it for uptime monitoring or App Service warmup.

---

## 10. Safety note

This software places **real financial orders** in Live mode. Always test in
**Paper** mode first, use sensible stop-losses, and understand that trading in
derivatives carries risk. You are responsible for your own trades.

