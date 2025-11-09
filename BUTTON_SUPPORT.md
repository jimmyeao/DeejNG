# Physical Button Support - Implementation Summary

## Overview

DeejNG now supports optional physical buttons on your controller for actions like play/pause, mute, and media control. Button data is sent alongside slider values via the serial protocol.

## Serial Protocol

### Data Format

Button values are appended after slider values, separated by the pipe character (`|`):

```
slider1|slider2|slider3|slider4|slider5|button1|button2
```

**Example with 5 sliders and 2 buttons:**
```
123|200|90|124|54|0|1
```

- First 5 values (123, 200, 90, 124, 54) = Slider positions (0-1023)
- Last 2 values (0, 1) = Button states (0=not pressed, 1=pressed)

### Button Values

- `0` = Button not pressed (or released)
- `1` = Button pressed

### Backward Compatibility

If you don't configure buttons (NumberOfButtons = 0), the system works exactly as before. The serial protocol is fully backward compatible.

## Configuration

### Settings Window

1. Open **Settings** from the main window
2. Scroll to the **Physical Buttons** section
3. Set **Number of Buttons** (0-8)
4. For each button, configure:
   - **Action**: What the button should do
   - **Target Channel**: Which channel to affect (for channel-specific actions)

### Available Actions

1. **None** - Button does nothing
2. **Media Play/Pause** - Simulates media play/pause key
3. **Media Next** - Skip to next track
4. **Media Previous** - Skip to previous track
5. **Media Stop** - Stop media playback
6. **Mute Channel** - Toggle mute for a specific channel (requires target channel)
7. **Global Mute** - Toggle mute for all channels
8. **Toggle Input/Output** - Reserved for future use (not yet implemented)

### Example Configuration

**For a 5-channel mixer with 2 buttons:**

- **Number of Buttons**: 2
- **Button 1**:
  - Action: Media Play/Pause
  - Target Channel: N/A
- **Button 2**:
  - Action: Mute Channel
  - Target Channel: 1

## Arduino Example Code

```cpp
const int NUM_SLIDERS = 5;
const int NUM_BUTTONS = 2;
const int sliderPins[] = {A0, A1, A2, A3, A4};
const int buttonPins[] = {2, 3}; // Digital pins for buttons

void setup() {
  Serial.begin(9600);

  // Setup button pins with pullup resistors
  for (int i = 0; i < NUM_BUTTONS; i++) {
    pinMode(buttonPins[i], INPUT_PULLUP);
  }
}

void loop() {
  // Read and send slider values
  for (int i = 0; i < NUM_SLIDERS; i++) {
    int value = analogRead(sliderPins[i]);
    Serial.print(value);
    Serial.print("|");
  }

  // Read and send button values
  for (int i = 0; i < NUM_BUTTONS; i++) {
    // Note: INPUT_PULLUP means LOW = pressed, HIGH = not pressed
    int buttonState = digitalRead(buttonPins[i]) == LOW ? 1 : 0;
    Serial.print(buttonState);

    if (i < NUM_BUTTONS - 1) {
      Serial.print("|");
    }
  }

  Serial.println(); // End of line
  delay(10);
}
```

## Technical Implementation

### New Files Created

1. **Models/ButtonAction.cs** - Enum defining available button actions
2. **Models/ButtonMapping.cs** - Model for button configuration
3. **Services/ButtonActionHandler.cs** - Handles button action execution

### Modified Files

1. **Classes/AppSettings.cs** - Added `NumberOfButtons` and `ButtonMappings` properties
2. **Services/SerialConnectionManager.cs** - Added button parsing and event raising
3. **MainWindow.xaml.cs** - Added button event handling and layout configuration
4. **Dialogs/SettingsWindow.xaml** - Added button configuration UI
5. **Dialogs/SettingsWindow.xaml.cs** - Added button configuration logic

### Key Features

- **Debouncing**: Buttons only trigger on press (not on release) to prevent double-triggering
- **Media Key Simulation**: Uses Windows keyboard simulation (keybd_event) for media keys
- **Channel-Specific Actions**: Some actions (like Mute Channel) can target specific channels
- **Profile Support**: Button configuration is saved per profile
- **Hot Reload**: Changing button configuration in settings takes effect immediately

## Troubleshooting

### Buttons Not Working

1. **Check Configuration**:
   - Open Settings → Physical Buttons
   - Verify NumberOfButtons matches your hardware
   - Verify button mappings are set

2. **Check Serial Data**:
   - In DEBUG mode, check the console for `[Serial] Button X pressed` messages
   - Verify your Arduino is sending the correct number of values

3. **Check Serial Format**:
   - Format must be: `slider1|slider2|...|button1|button2|...`
   - Button values must be 0 or 1
   - Total values = Number of Sliders + Number of Buttons

### Media Keys Not Working

- Media keys work globally and control the currently focused media player
- Some applications may not respond to simulated media keys
- Ensure your media player is running and has media loaded

### Mute Not Working

- Verify the target channel index is correct (1-based in UI, 0-based internally)
- Ensure the channel has audio targets assigned
- Check that the channel exists (don't target channel 5 if you only have 4 channels)

## Debug Logging

When built in DEBUG mode, the application logs button events:

```
[Serial] Configured for 5 sliders and 2 buttons
[Serial] Button 0 pressed
[ButtonAction] Executing MediaPlayPause for button 0
[ButtonAction] Sent media key: 0xB3
```

## Future Enhancements

Potential improvements for future versions:

1. **Toggle Input/Output** - Complete implementation for switching channels between input/output devices
2. **Volume Adjustment** - Buttons to increase/decrease volume on specific channels
3. **Profile Switching** - Use buttons to switch between profiles
4. **Macro Support** - Execute multiple actions with a single button press
5. **Long Press Detection** - Different actions for short vs. long press
6. **Button Combinations** - Hold one button and press another for advanced actions

## Implementation Notes

- Button state is tracked internally to detect press/release transitions
- Only press events trigger actions (release is ignored)
- Media keys use Win32 `keybd_event` API for maximum compatibility
- Button actions execute on the UI thread to safely interact with WPF controls
- The serial layout (slider count + button count) is configured automatically when sliders are generated

## UI Button Indicators (New!)

The main window now shows visual indicators for configured buttons:

- **Button indicators panel** appears below the sliders when buttons are configured
- Each button shows:
  - Icon representing the action (⏯ for Play/Pause, 🔇 for Mute, etc.)
  - Button label (BTN 1, BTN 2, etc.)
  - Action description (e.g., "Play/Pause", "Mute Ch1")
- **Visual feedback**: Button lights up when pressed (highlighted in accent color)
- **Tooltips**: Hover over a button to see its full configuration

The panel automatically hides when no buttons are configured.
