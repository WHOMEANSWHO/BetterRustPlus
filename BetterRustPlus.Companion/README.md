# Companion pairing foundation

This .NET 8 library links the existing public account implementation from `Avalonia/src` rather than copying the unavailable private authentication provider. It preserves the upstream GPL-3.0 license and attribution in `Avalonia/NOTICE.md`. Public FCM registration is provided by the MIT-licensed `RustPlusApi.Fcm.Registration` package.

The service validates the login callback, checks the resulting token with Facepunch, registers push credentials, saves the paired configuration atomically and rolls back a new device registration on save failure. The linked offline tests use fake HTTP, fake browser and temporary fixture data; they do not verify live pairing.

This foundation is not connected to the legacy WPF pairing button yet. `WpfCompanionLoginBrowser` is an unverified browser adapter; the current public compatibility fallback still reports pairing unavailable. Live pairing, WPF integration, credential lifetime and a Linux/VPS handoff remain future work.

Run offline account tests with `dotnet test BetterRustPlus.Companion.Tests/BetterRustPlus.Companion.Tests.csproj`.
