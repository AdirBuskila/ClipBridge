# ClipBridge

**Copy on your Windows PC, paste on your Android device, and the other way around.** Text and screenshots, including Lightshot and anything else that copies an image.

ClipBridge exists because Samsung Flow's shared clipboard kept hanging on "connecting Wi-Fi Direct". It doesn't use Wi-Fi Direct and doesn't need an account, a cloud service, or a server. Your clips go straight between your PC and your device, encrypted.

## Features

- **Copy and paste, nothing else to do.** Press Ctrl+C on the PC and paste on the device.
- **Send from the device too.** Tap **Send clipboard to PC** in the notification, or use the Quick Settings tile, the share menu, or the menu over selected text. Then paste on the PC.
- **Text and images.** Hebrew and other non-English text works too.
- **Wi-Fi or Bluetooth.** It uses Wi-Fi when it can and falls back to Bluetooth when it can't.
- **Survives sleep and dropouts.** It reconnects by itself. The newest copy waits for up to 10 minutes and is delivered when the other side is back.
- **Encrypted and paired.** Only a device with your pairing code can connect.
- **Respects password managers.** Copies marked private by the app that made them stay on the PC.

## Sending from the device to the PC

Android doesn't let apps read the clipboard in the background, so ClipBridge can't notice a copy on the device by itself the way it does on the PC. Instead, copy as usual and then send it with one tap:

- **Notification:** pull down the notification shade and tap **Send clipboard to PC** on the ClipBridge notification.
- **Quick Settings tile:** edit your Quick Settings, add the **Send to PC** tile, and tap it after copying.
- **Selected text:** select text in any app, open the menu that appears (tap **⋮** if needed), and choose **Send to PC**. You don't need to copy first.
- **Share menu:** share a picture or text and choose **Send to PC**.
- **The app:** tap **Send clipboard to PC** in ClipBridge.

A "Sent to your PC" message confirms it's on the PC clipboard. Android may also show a short note that ClipBridge read your clipboard. Photos are sent as they are when the PC can read them (PNG, JPEG, GIF, BMP), and are converted to PNG otherwise.

## Setup

You need a Windows 10 or 11 PC (64-bit) and an Android 8.0+ device.

### 1. On the PC

1. Download [ClipBridge.exe](https://github.com/AdirBuskila/ClipBridge/releases/latest/download/ClipBridge.exe) and put it somewhere permanent, such as a `ClipBridge` folder in Documents. No install or .NET is needed.
2. Run it. Windows SmartScreen will probably warn you because the app isn't commercially signed. Choose **More info > Run anyway**.

ClipBridge lives in the system tray, next to the clock, and starts with Windows. You may need to click the **^** arrow to see its icon.

### 2. On the Android device

1. Download [ClipBridge.apk](https://github.com/AdirBuskila/ClipBridge/releases/latest/download/ClipBridge.apk) and open it. Android will ask you to allow installs from that source, and Play Protect may warn you because the app isn't from the Play Store.
2. Open ClipBridge and allow **notifications** and **Nearby devices**.
3. Tap **Keep running in the background** and allow it.

### 3. Pair them

Type the 8-character code from the device into the window on the PC. If the window isn't open, right-click the tray icon and choose **Enter pairing code...**

### 4. Try it

When the tray icon is green, copy something on the PC and paste it on the device. To go the other way, copy on the device, tap **Send clipboard to PC** in the ClipBridge notification, and paste on the PC.

Both apps need to be version 1.3 or newer to send from the device. An older version on either side keeps working, but only from PC to device.

**To use Bluetooth,** pair the PC and the device in their normal Bluetooth settings first.

## Tray icon

| Color | Meaning |
| --- | --- |
| Green | Connected. Right-click to see whether it's over Wi-Fi or Bluetooth. |
| Orange | Looking for the device |
| Red | Not paired yet, or the pairing code is wrong |
| Gray | Paused |

## Wi-Fi and Bluetooth

| Connection | Speed | When to use it |
| --- | --- | --- |
| **Same Wi-Fi network** | Instant | Home networks |
| **PC's mobile hotspot** | Instant | Networks that block devices from reaching each other |
| **Bluetooth** | About 28 KB/s when measured: roughly 4 seconds for a 100 KB screenshot, 35 seconds for 1 MB | Anywhere, with no network at all |

Dorm, campus, office, and public Wi-Fi often block devices from talking to each other, even when both are on the same network. ClipBridge can't get through that block, so use the hotspot or Bluetooth there.

Once connected over Bluetooth, ClipBridge stays on Bluetooth until that connection drops, even if Wi-Fi becomes available.

## Troubleshooting

**The icon stays orange**
- Check that both devices are on the same network. Being on the same Wi-Fi isn't always enough, as explained above.
- Turn on the PC's hotspot under **Settings > Network & Internet > Mobile hotspot** and connect the device to it. Or use Bluetooth.
- Open the app on the device. It should say **Bluetooth: ready**.
- If it still can't connect, right-click the tray icon and choose **Open log** to see what's failing.

**The icon is red and says "Wrong pairing code"**
Enter the code shown on the device again from the tray menu.

**The hotspot doesn't appear in the device's Wi-Fi list**
Windows may have turned it off, since it does that when nothing is connected. If it's on, your PC's Wi-Fi may have moved to a 5 GHz channel the device can't use for this. Try setting the hotspot's band to 2.4 GHz, or use Bluetooth.

**It stops working when the device has been idle for a while**
Android may be closing the app to save battery. On Samsung, go to **Settings > Battery > Background usage limits** and add ClipBridge to **Never sleeping apps**. Other brands have similar settings.

**An app won't let me paste the image**
Some apps only accept pasted text. Use **Share image** in the ClipBridge notification instead.

**An old copy didn't arrive**
Copies older than 10 minutes are dropped rather than overwriting your clipboard later. Only the newest copy is kept while disconnected.

**Nothing happens when I send from the device**
Make sure both apps are version 1.3 or newer and the tray icon is green. If the device says "Will send when your PC connects", the clip goes as soon as the PC is back, within 10 minutes. The tray menu's **Last received** line shows when the PC last got something.

## Security and privacy

- Clips travel directly between your devices and nothing is sent to any server.
- Both sides prove they know the pairing code before anything is sent, and everything after that is encrypted with AES-256-GCM using keys unique to each connection.
- The code is run through PBKDF2 (300,000 rounds) to slow down guessing. The PC stores it encrypted for your Windows user with DPAPI.
- Recent screenshots from the PC, up to three, stay in the Android app's private cache until replaced.

**Please keep in mind:**
- This is a hobby project and **hasn't had an independent security audit**. The cryptography uses standard, well-known building blocks, but they've been combined specifically for this app.
- Anything you copy on the PC is sent to the device unless the app that copied it marks it private. Many password managers do, but check yours, or use **Pause** in the tray menu.
- Nothing on the device is sent unless you send it. Whatever you do send replaces the PC's clipboard.
- An 8-character code is convenient rather than unbreakable. If you think it has leaked, get a new one by clearing the app's data in Android's app settings, then enter the new code on the PC.

## What's been tested

Developed and tested on Windows 10 with a Samsung Galaxy Tab S8. Other Android brands should work but haven't been tried, and they may need battery settings changed. Linux, macOS, and iOS aren't supported.

## Uninstall

- **PC:** right-click the tray icon, uncheck **Start with Windows**, and choose **Exit**. Then delete `ClipBridge.exe` and the `%APPDATA%\ClipBridge` folder.
- **Android:** uninstall it like any other app.

## Building from source

You need the .NET 10 SDK, a JDK (17 or newer), and the Android SDK with `platforms;android-34` and `build-tools;34.0.0`. Then run:

```powershell
./build.ps1
```

The output goes to `dist\`. The first build creates a signing key in `%USERPROFILE%\.clipbridge-signing`, outside the project. Keep that folder safe, because Android only installs updates signed with the same key.

To run the tests:

```powershell
# Runs the PC's networking code against the Android app's networking code.
javac -nowarn -d build/interop-classes android/src/com/clipbridge/Protocol.java android/src/com/clipbridge/BridgeServer.java android/test/InteropServer.java
dotnet run --project pc/Tests/Tests.csproj -c Release -- build/interop-classes

# Runs the real app against the real Windows clipboard. Exit ClipBridge first.
powershell -Sta -File pc/Tests/e2e.ps1

# Checks real Bluetooth against a paired device without touching its clipboard.
# The result is saved to %APPDATA%\ClipBridge\bluetooth-test.txt.
dist\ClipBridge.exe --test-bluetooth
```

### How it's put together

| Path | What's there |
| --- | --- |
| `pc/App` | Tray app, clipboard watcher, Bluetooth, settings |
| `pc/Core` | Connection, encryption, and finding the device. No Windows-only code. |
| `pc/Tests` | Automated tests |
| `android/src` | Android app. `Protocol.java` and `BridgeServer.java` have no Android dependencies, so the tests can run them on a PC. |

The device listens and the PC connects to it. Clips then travel both ways over that one connection. The device only sends its own clips once the PC's pings show that it can read them, so an older PC is never sent one. Android has no inbound firewall, while Windows blocks inbound connections on networks marked Public, so this avoids any firewall setup. It uses TCP port 47821 and UDP port 47822 on Wi-Fi.

## License

[MIT](LICENSE). Bluetooth on the PC uses [32feet.NET](https://github.com/inthehand/32feet).
