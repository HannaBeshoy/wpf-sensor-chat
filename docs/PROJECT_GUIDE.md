# Project guide

How the chat meets each grade of the Telecommunication Systems requirements, and the reasons behind the design.

## 1. Sockets and framing (grade 2)

`TcpListener` accepts connections; each accepted `TcpClient` gives a `NetworkStream`. TCP guarantees the bytes arrive complete and in order, but not in the pieces they were sent in: one `Write` of 100 bytes may arrive as 60 + 40, and two small writes may arrive as one read. A message format therefore needs boundaries. Here each message is length-prefixed: 4 bytes of length, then the JSON. `FrameCodec.ReadAsync` reads exactly 4 bytes, then exactly that many more, in a loop. The first test feeds two messages through a stream that returns one byte per read and checks both arrive intact.

A clean exit is a Logout frame followed by closing the socket; a crash is the read returning 0 bytes or throwing. Both lead to the same `finally` block on the server, which removes the user and tells everyone.

## 2. Asynchronous operation

The course example compares downloading web pages one after another, with `await` one after another, and with `Task.WhenAll`. The server uses the same tools: `await stream.ReadAsync` releases the thread while a client is silent, `Task.Run` starts each client's loop, and the client window never blocks its UI thread because every network call is awaited. Events raised from network tasks are passed to the UI with `Dispatcher.InvokeAsync`, because WPF controls may only be touched from the thread that created them.

## 3. Login (grade 3)

"Managing, creating and maintaining the information is up to everyone": here a JSON file of accounts, each with a salt and a PBKDF2 hash. A hash is one-way, so a stolen users file does not reveal the passwords; the salt makes equal passwords hash differently; 100,000 iterations make guessing slow. `FixedTimeEquals` compares without stopping at the first different byte. Before login the server accepts only Register and Login; after 5 failed logins on one connection it hangs up.

## 4. Private messages (grade 4)

The server keeps the online sessions in a `ConcurrentDictionary` by user name. A Private message is written only to the addressee's session (and back to the sender as confirmation). The test logs in three users and checks the third one never sees it.

## 5. File transfer (grade 5)

A file goes through the server in chunks of 48 KiB (64 KiB once base64-encoded inside JSON), each with its offset, so the receiver can check nothing is missing or out of order. The last chunk carries the SHA-256 of the whole file; the receiver recomputes it and refuses a damaged file. The server only relays: it checks the announced size (10 MiB at most), the chunk size and the order, and strips any path from the file name, but never stores the file.

## 6. The ESP32 as a user

The protocol is simple enough for a microcontroller: the ESP32 writes 4 length bytes and a JSON string made with `snprintf`, and finds the fields it needs in the replies with `strstr`. It logs in with its own account, posts Sensor messages every 5 seconds, reconnects and logs in again if the connection drops, and answers a private "status" message with its uptime and Wi-Fi signal strength.

## Notes for the three tracks

- **Embedded systems and edge AI.** The same length-prefixed framing is how embedded devices talk to gateways over TCP; the ESP32 needs about 2 KB of buffer and no JSON library.
- **FPGA to semiconductors.** Framing with a length header is also how hardware links (Ethernet, PCIe packets) tell a receiver how many bytes belong together; a receiver that trusts an unchecked length is a classic hardware and software bug.
- **Product security.** Everything that crosses the network is checked: lengths, sizes, order, file names, who is sending. The remaining weakness is the network itself: plain TCP can be read by anyone on it, so a production version wraps the stream in TLS (`SslStream`) with a server certificate.
