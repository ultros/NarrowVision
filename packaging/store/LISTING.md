# Microsoft Store listing

- **Product name** NovusVision
- **Publisher** Novus Mercatura
- **Language** English (United States)

## Short description

Keep a small area of your Windows desktop visible and review mouse clicks before they reach an app.

## Description

NovusVision gives you control over what is visible on your Windows 11 desktop. A cursor-following overlay makes the surrounding screen black while you read or work in a clear area. Adjust its size, shape, darkness, and display coverage for shared spaces or focused work.

Enable Click Guard to review a mouse click before it reaches a link, button, or other control. Choose Cancel to discard the click or Proceed once to send it to the original target. Unanswered prompts expire automatically. Use the tray menu or set your own keyboard shortcuts to switch modes or pause.

The overlay can appear in a full-display share that captures the composited desktop. An individual window share can still show the underlying content. Click Guard relies on the information Windows and each app provide about the target.

## Search terms

screen privacy, shoulder surfing, desktop overlay, cursor spotlight, click confirmation, focus

## Links

- [Support and product information](https://novusmercatura.com)
- [Privacy policy](https://github.com/ultros/NovusVision/blob/main/PRIVACY.md)

## Certification notes

NovusVision is a full-trust WPF desktop app. It uses a mouse hook to intercept configured mouse buttons when the user enables Click Guard. It uses Windows UI Automation to provide context for the pending click. The tray app draws a local overlay and registers user-configured global shortcuts. It stores preferences locally and requires no account or internet connection.

Click Guard and Start with Windows are initially off. To try Click Guard, enable it in settings, close settings to the tray, and click a harmless control in another app. Cancel discards the click. Proceed once sends it to the original target. Right-click the NovusVision tray icon to exit the app.

## Package submission

Reserve NovusVision in [Microsoft Partner Center](https://partner.microsoft.com/dashboard/windows/overview). Copy the case-sensitive package identity name, publisher, and publisher display name from **Product management → Product identity**. Build the x64 MSIX with those values using [Build-Msix.ps1](../Build-Msix.ps1) and upload that package to the reserved product. Microsoft signs the package after it passes certification.

Use the [dashboard screenshot](screenshots/dashboard.png) in the desktop listing. The app supports English (United States), Windows 11 desktop, and x64 in this release.
