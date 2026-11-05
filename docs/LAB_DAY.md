# Lab-day test sheet (20 minutes)

**Need:** a Windows laptop with the .NET 8 SDK (or Visual Studio 2022), the ESP32 with the sensors of the network-node project, a Wi-Fi network shared by the laptop and the ESP32.

1. Build: `dotnet build SensorChat.Windows.sln`. Start `ChatServer.exe` (src/Chat.Server.Wpf/bin/...), press Start (allow it through the firewall for private networks).
2. Start `ChatClient.exe` twice. In each, register an account, log in. Register `sensor-node` too (password as in the ESP32's secrets.h).
3. Grade 2–4 check: a message from one window appears in the other; tick "only to the selected user" and send: it appears only there. Close one client: the other shows "... left the chat".
4. Grade 5 check: "Send file..." a photo to the other user; it is saved in Documents\SensorChat.
5. ESP32: fill in secrets.h (Wi-Fi, the laptop's IP from `ipconfig`, port 5050, sensor-node and its password), upload. Within 5 s every chat window shows "Sensor node: sensor-node at ...: 23.4 °C, 41.7 cm, light 63 %". Send it the private message `status`: it answers with uptime, RSSI and temperature.
6. Unplug the ESP32: the windows show "sensor-node left the chat". Plug it in: it logs in again by itself.

**Record:** screenshots of the two chat windows (with the sensor line and a private message), the server log, and a photo of the ESP32.
