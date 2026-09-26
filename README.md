# DeckLink Switcher
![DeckLink Switcher](image.png)

A lightweight and efficient WPF application designed for Blackmagic DeckLink devices. It serves as an intuitive 4-input video switcher with built-in test signals and live audio monitoring.

## Features

*   **Live Video Routing:** Connect up to 4 DeckLink inputs and route them dynamically to a primary DeckLink output.
*   **Audio Monitoring:** Real-time Left and Right audio channel level meters for all 4 inputs as well as the main program output.
*   **Live Previews:** Visual previews of all 4 input sources side-by-side. 
*   **Click-to-Switch:** Easily change the program output by clicking directly on the input video previews or the corresponding input buttons below them.
*   **Synthetic Sources:**
    *   **Color Bars + Tone:** Generate an 8-stripe SMPTE-style UYVY color bar pattern with a continuous 1kHz sine wave audio tone for calibrating equipment.
    *   **Matte Color Generator:** Send a full-screen solid matte color directly to the output. Supports on-the-fly switching between Black, White, Red, Green, Blue, Yellow, Cyan, and Magenta.
*   **Robust COM Handling:** Seamlessly interfaces with the Blackmagic DeckLink SDK using Multithreaded Apartment (MTA) threading.

## Prerequisites

*   Windows operating system
*   .NET 8.0 SDK (or higher)
*   Blackmagic Desktop Video software installed (provides the required `DeckLinkAPI.dll` COM interfaces)
*   Supported Blackmagic DeckLink hardware (e.g., DeckLink Duo 2, Quad 2, 4K Extreme)

## Building the Application

To build the application locally:

1. Clone this repository.
2. Ensure you have the .NET SDK installed.
3. Run the following command in the project directory:

```bash
dotnet build
```

The compiled application and its dependencies will be placed in the `bin/Debug/netX.0-windows/` directory.

## Usage

1. Open `DecklinkSwitcher.exe`.
2. Select your designated **Output Device** from the right-side control panel.
3. Map your physical DeckLink inputs to **Input 1**, **Input 2**, **Input 3**, and **Input 4**.
4. Click **Apply Settings**. The application will initialize the DeckLink API and start capturing feeds.
5. Click on an Input's preview image or button to route its signal to the output.
6. Click the **Color Bars + Tone** or **Matte Color** buttons to utilize the synthetic testing feeds.
