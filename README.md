# WPF Sensor Chat

[![CI](https://github.com/HannaBeshoy/wpf-sensor-chat/actions/workflows/ci.yml/badge.svg)](https://github.com/HannaBeshoy/wpf-sensor-chat/actions/workflows/ci.yml)

**Telecommunication Systems course project** · University of Debrecen, Faculty of Informatics · C# (.NET 8, WPF), TCP sockets, async/await, ESP32 · **server, client and protocol tested on a PC; ESP32 client on the December lab day**

A chat application at the course's top grade, and one step beyond it: a C# WPF server and WPF clients with login, private messages and file transfer, plus an ESP32 sensor node that logs in like any other user and posts its readings into the chat.

| Course grade | Requirement | Here |
| --- | --- | --- |
| 2 | clients connect, send messages, exit cleanly | broadcast chat; a client leaving is announced and removed from every user list |
| 3 | login with user name and password | accounts with PBKDF2-SHA256 salted hashes; registration from the client; 5 wrong tries close the connection |
| 4 | private messages to a chosen user | select a user, tick "only to the selected user": nobody else receives it |
| 5 | file transfer between clients | files up to 10 MiB, sent in 48 KiB chunks through the server, checked with SHA-256 on arrival |
| beyond | | an ESP32 posts temperature, distance and light every 5 s; every window shows the latest reading |

- [x] Protocol library, server and client libraries (.NET 8, no NuGet packages)
- [x] WPF server window and WPF client (login window and chat window); built by GitHub Actions on Windows
- [x] Headless server for Linux or a Raspberry Pi (`Chat.Server.Cli`)
- [x] **14 tests**: framing, user store, every grade requirement end to end over real sockets, the ESP32's hand-written frames, garbage on the socket, 300 concurrent messages
- [x] ESP32 client firmware
- [ ] Lab day: the ESP32 posting into the chat over Wi-Fi ([test sheet](docs/LAB_DAY.md))

## Protocol

TCP is a byte stream, so every message is a frame: a 4-byte big-endian length, then UTF-8 JSON.

```text
00 00 00 1C  {"type":"Chat","text":"hi"}           client -> server
00 00 00 4F  {"type":"Chat","from":"anna","text":"hi","time":"2026-11-05T21:30:00+01:00"}   server -> everyone
```

| Type | Direction | Fields |
| --- | --- | --- |
| Register, Login | client → server | user, password |
| LoginResult | server → client | ok, text |
| UserList | server → all | users |
| Chat | client → server → all | text (the server adds from and time) |
| Private | client → server → one user | to, text |
| FileChunk | client → server → one user | to, fileId, fileName, fileSize, offset, data (base64); sha256 on the last chunk |
| Sensor | ESP32 → server → all | temp, dist, light |
| System, Error | server → client | text |
| Logout | client → server | |

The ESP32 builds the same JSON with `snprintf`; one test sends exactly those bytes from a raw socket.

## Design

- **One async loop per client.** The server awaits each client's next frame; while a client is silent no thread is blocked, so a few threads serve many clients (the course's asynchronous-operation example, applied to sockets).
- **A send lock per client.** A broadcast and a private message may be sent to the same client at the same moment from two tasks; without the lock their bytes would interleave and break the framing. The concurrency test sends 300 messages from 3 clients at once and checks that every client receives all 300, each sender's in order.
- **The server sets the sender.** `from` and `time` are filled in by the server from the logged-in session, so a client cannot post as someone else.
- **Bounded input.** Frames over 1 MiB, negative lengths, bad JSON and cut frames close that client's connection without affecting the others; messages are limited to 4,000 characters, files to 10 MiB, chunks to 64 KiB, and chunks must arrive in order.
- **Passwords are never stored.** Each account has a random 16-byte salt and a PBKDF2-SHA256 hash with 100,000 iterations, compared in constant time; an unknown user name costs the same time as a wrong password, so timing does not reveal which names exist.
- **File names are cleaned.** Only the last part of the name is kept, whichever separator the sender used, so a received file cannot be written outside the download folder (the test sends `C:\Users\anna\secret\..\report.bin`).

Plain TCP carries the messages and the login password in clear text; on a real network the next step would be TLS (`SslStream` around the same stream), noted in the project guide.

## Running it

```bash
dotnet run --project tests/Chat.Tests              # the tests (any OS, .NET 8 SDK)
dotnet run --project src/Chat.Server.Cli -- --port 5050 --users users.json
```

On Windows, open `SensorChat.Windows.sln` in Visual Studio 2022 (or `dotnet build SensorChat.Windows.sln`), start **Chat.Server.Wpf**, press Start, then start **Chat.Client.Wpf** two or three times: register an account in each, log in, and chat. `wpf-sensor-chat.sln` holds the projects that build on every OS.

**ESP32:** register the account `sensor-node` from a client first. Copy `firmware/esp32_chat_client/secrets.example.h` to `secrets.h`, fill in the Wi-Fi, the server's IP address and that account, and upload `esp32_chat_client.ino` (Arduino-ESP32 core 3.x).

## Repository layout

```text
src/Chat.Protocol      messages, framing, user store
src/Chat.Server        the server (library)
src/Chat.Client        the client (library)
src/Chat.Server.Wpf    server window          src/Chat.Client.Wpf    login and chat windows
src/Chat.Server.Cli    headless server
tests/Chat.Tests       the tests (a console runner, no packages)
firmware/              ESP32 chat client
docs/                  project guide, lab-day test sheet
```

## Author

Beshoy Hanna · Computer Science Engineering BSc, University of Debrecen · [LinkedIn](https://www.linkedin.com/in/beshoy-hanna-8b52213a0)
