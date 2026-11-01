# Companion pairing foundation

This .NET 8 library links the existing public account implementation from `Avalonia/src` rather than copying the unavailable private authentication provider. It preserves the upstream GPL-3.0 license and attribution in `Avalonia/NOTICE.md`. Public FCM registration is provided by the MIT-licensed `RustPlusApi.Fcm.Registration` package.

The service validates the login callback, checks the resulting token with Facepunch, registers push credentials, saves the paired configuration atomically and rolls back a new device registration on save failure. The linked offline tests use fake HTTP, fake browser and temporary fixture data; they do not verify live pairing.

The WPF pairing flow now uses this service and an isolated WebView2 login adapter. Registration, renewal and failure handling have offline tests; no real Steam login or Rust+ connection has been verified. Credential lifetime and Linux/VPS handoff need further work.

Run offline account tests with `dotnet test BetterRustPlus.Companion.Tests/BetterRustPlus.Companion.Tests.csproj`.
