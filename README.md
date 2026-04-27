# NetLubricant

Unofficial .NET client for Netgear LTE modems, reverse-engineered from the
device's web admin interface for interoperability purposes.

## Features

Supports eternalegypt-style Netgear LTE modems (LM1200, LB2120, LB1120,
MR1100, MR6110): login, SMS read/send/delete, modem info, data-usage,
reboot, LTE re-attach, and reachability probing.

## Install

```sh
dotnet add package NetLubricant
```

Targets `net10.0`.

## Quick start

```csharp
using NetLubricant;

using var client = new NetgearModemClient(
    host:     "192.168.5.1",
    password: "<admin-password>");

await client.LoginAsync();

// Read inbox
var inbox = await client.ListSmsAsync();
foreach (var msg in inbox)
    Console.WriteLine($"{msg.Sender}: {msg.Text}");

// Send SMS
await client.SendSmsAsync("+886912345678", "hello from .NET");

// Wait for an OTP-style message (e.g. 3DS)
var otp = await client.WaitForSmsAsync(
    predicate: m => m.Sender == "PaymentBank",
    timeout:   TimeSpan.FromMinutes(2));

// Modem diagnostics & data usage
var info  = await client.GetInfoAsync();
var usage = await client.GetUsageAsync();
```

All public types live under the `NetLubricant` namespace.

## Supported devices

Confirmed against firmware that exposes the eternalegypt-style admin API:

| Model  | Notes                       |
| ------ | --------------------------- |
| LM1200 | primary test device         |
| LB2120 | same protocol as LM1200     |
| LB1120 | same protocol as LM1200     |
| MR1100 | Nighthawk M1, same protocol |
| MR6110 | 5G variant, mostly the same |

Other models in the same family will likely work; per-device differences
typically appear as missing fields on `ModemInfo` rather than failures.

## Disclaimer

This project is **not affiliated with, endorsed by, or sponsored by NETGEAR
Inc.** or any other vendor whose devices are supported. All trademarks are
the property of their respective owners. Device protocols were observed by
sending requests from a normal browser session and inspecting the publicly
accessible JSON responses; no firmware was decompiled and no DRM/TPM was
circumvented in the process.

Use at your own risk. The vendor's EULA may restrict reverse engineering;
running this code may violate that contract even though it does not violate
copyright. Verify your local laws and the device EULA before use.

## License

MIT — see [LICENSE](LICENSE).
