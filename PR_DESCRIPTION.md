# Add Hardware Button Support and Inline Mute Functionality

## Summary

This PR adds two major hardware control features to DeejNG:
1. **Auto-Detected Hardware Buttons** - Physical buttons automatically detected via distinct value ranges (10000/10001)
2. **Inline Mute Trigger** - Quick channel muting by sending 9999 from the hardware controller

These features enable sophisticated physical control interfaces beyond simple sliders, with self-documenting, robust serial protocols.

---

## Feature 1: Auto-Detected Hardware Buttons

### Overview

DeejNG now auto-detects and supports physical buttons from your Arduino/controller. Buttons use distinct value ranges (10000=OFF, 10001=ON) that are instantly recognizable and self-documenting - no manual configuration of button counts needed!

### Serial Protocol

**Format:**
```
<slider1>|<slider2>|...|<sliderN>|<button1>|<button2>|...|<buttonM>
```

**Button Values:**
- `10000` = Button not pressed (OFF)
- `10001` = Button pressed (ON)

**Slider Values:**
- `0-1023` = Normal ADC range
- `9999` = Inline mute trigger (see Feature 2)

**Example with 5 sliders and 3 buttons:**
```
512|1023|0|800|400|10000|10001|10000
```
- Sliders: `512|1023|0|800|400` (5 slider values)
- Buttons: `10000|10001|10000` (3 buttons - button 1 is pressed)

### Why 10000/10001?

✅ **Self-Documenting** - Values instantly distinguish buttons from sliders
✅ **Auto-Detection** - App automatically counts and separates sliders vs buttons
✅ **No Configuration** - No need to manually set "number of buttons"
✅ **Robust** - Works even if channel count changes dynamically
✅ **Debug-Friendly** - Serial monitor shows clear distinction between data types

### Configuration

**In Settings → Physical Buttons:**

Configure up to 8 button actions (buttons auto-detect when hardware sends data):

#### Transport Controls
- **Play/Pause** - Toggle media playback
- **Next Track** - Skip to next track
- **Previous Track** - Skip to previous track

#### Mute Controls
- **Mute Channel 0-N** - Toggle mute for specific slider channel
- **Mute All** - Toggle mute for all channels

### UI Indicators

- Button states displayed as colored indicators below channel sliders
- **Green** = Button pressed
- **Gray** = Button not pressed
- Hover to see button action assignments
- Only appears when buttons are configured

### Hardware Example (Arduino)

```cpp
// 5 sliders + 3 buttons example
const int NUM_SLIDERS = 5;
const int NUM_BUTTONS = 3;

int sliderPins[NUM_SLIDERS] = {A0, A1, A2, A3, A4};
int buttonPins[NUM_BUTTONS] = {2, 3, 4}; // Digital pins with INPUT_PULLUP

void setup() {
  Serial.begin(9600);
  for (int i = 0; i < NUM_BUTTONS; i++) {
    pinMode(buttonPins[i], INPUT_PULLUP);
  }
}

void loop() {
  // Send slider values (0-1023)
  for (int i = 0; i < NUM_SLIDERS; i++) {
    Serial.print(analogRead(sliderPins[i]));
    Serial.print('|');
  }

  // Send button states (10000 or 10001)
  for (int i = 0; i < NUM_BUTTONS; i++) {
    // INPUT_PULLUP is inverted: LOW when pressed
    bool pressed = (digitalRead(buttonPins[i]) == LOW);
    Serial.print(pressed ? 10001 : 10000);

    if (i < NUM_BUTTONS - 1) {
      Serial.print('|');
    }
  }

  Serial.println(); // End of message
  delay(10);
}
```

---

## Feature 2: Inline Mute Trigger

### Overview

Channels can be instantly muted by sending **9999** from the hardware controller. This enables hardware-level mute buttons without using the button protocol - perfect for momentary mute controls.

### How It Works

**Normal operation:**
```
512|768|400|1020|600
```
All channels update normally.

**Mute Channel 2:**
```
512|768|9999|1020|600
```
- Channels 0, 1, 3, 4: Update normally
- **Channel 2: Instantly muted** (slider position frozen, audio muted)

**Unmute Channel 2:**
```
512|768|400|1020|600
```
- Channel 2: Automatically **unmutes** and resumes at value `400`

### Behavior

✅ **When 9999 received:**
- Channel immediately muted
- Slider UI stays at current position (doesn't jump to 9999)
- Audio output muted
- State tracked internally

✅ **When normal value resumes (0-1023):**
- Channel automatically unmutes
- Slider updates to new position
- Audio output resumes

✅ **Multiple channels:**
- Each channel independently inline-muted
- Example: `9999|9999|500|1020|9999` mutes channels 0, 1, and 4

### Use Cases

**1. Momentary Mute Button per Channel:**
```cpp
// Hold button to mute, release to unmute
int muteButton = digitalRead(MUTE_PIN_1);
if (muteButton == LOW) {  // Pressed (INPUT_PULLUP)
  Serial.print("9999");   // Mute while held
} else {
  Serial.print(analogRead(A0));  // Normal slider control
}
```

**2. Toggle Mute with Encoder Click:**
```cpp
bool channel1Muted = false;

if (encoderButtonClicked()) {
  channel1Muted = !channel1Muted;
}

Serial.print(channel1Muted ? 9999 : analogRead(A0));
```

**3. Mix Both Features:**
```
512|9999|400|10000|10001
```
- Slider 0: Normal (512)
- Slider 1: Inline-muted (9999)
- Slider 2: Normal (400)
- Button 0: Not pressed (10000)
- Button 1: Pressed (10001)

### Protocol Validation

Both button values (10000/10001) and inline mute (9999) are recognized as valid DeejNG protocol and won't cause connection validation failures.

---

## Technical Implementation

### Files Changed

**Hardware Button Support:**
- `Services/SerialConnectionManager.cs` - Auto-detection of buttons by value range, protocol validation
- `MainWindow.xaml.cs` - Button event handling, lazy initialization
- `Dialogs/SettingsWindow.xaml` - Updated UI with auto-detection info, removed manual count configuration
- `Dialogs/SettingsWindow.xaml.cs` - Simplified button configuration (8 slots, auto-detect)
- `Services/ButtonActionHandler.cs` - Transport control and channel mute logic
- `Models/ButtonAction.cs` - Button action enumeration
- `Models/ButtonIndicatorViewModel.cs` - UI indicator state management

**Inline Mute:**
- `MainWindow.xaml.cs` - Inline mute detection and channel state tracking (9999 value handling)
- `Services/SerialConnectionManager.cs` - Protocol validation for 9999 and 10000/10001 values

### Backwards Compatibility

✅ **Fully backwards compatible:**
- Slider-only mode works exactly as before
- No breaking changes to existing serial protocol
- Settings files from older versions load correctly
- Apps without buttons send only slider data (0-1023)

---

## Protocol Value Ranges

| Value Range | Purpose | Example |
|------------|---------|---------|
| `0 - 1023` | Slider (normal ADC) | `512` = ~50% volume |
| `9999` | Inline mute trigger | Mute this specific channel |
| `10000` | Button OFF | Button not pressed |
| `10001` | Button ON | Button pressed |

**Auto-Detection Logic:**
- Values < 9999.5 → Slider data
- Values >= 9999.5 → Button or inline mute

---

## Testing

### Button Support
- [x] Send button data with 1-5 buttons using 10000/10001 values
- [x] Verify buttons auto-detected without configuration
- [x] Verify button indicators display in UI
- [x] Test transport controls (Play/Pause, Next, Previous)
- [x] Test channel mute assignments
- [x] Verify button state changes reflected in real-time
- [x] Test button configuration persistence

### Inline Mute
- [x] Send 9999 to individual channels
- [x] Verify channel mutes immediately
- [x] Verify slider position doesn't jump
- [x] Send normal value and verify automatic unmute
- [x] Test multiple channels muted simultaneously
- [x] Verify protocol validation accepts 9999

### Mixed Usage
- [x] Mix sliders, buttons (10000/10001), and inline mute (9999)
- [x] Change channel count dynamically
- [x] Handle rapid button press/release
- [x] Test with all channels inline-muted

---

## Configuration

No manual configuration required for button detection!

Buttons are automatically detected when hardware sends 10000/10001 values. Users can pre-configure button actions in Settings → Physical Buttons (up to 8 buttons supported).

**Settings Storage:**
- Button action mappings stored in `ButtonMappings` list
- Only saves mappings with configured actions (not "None")
- `NumberOfButtons` field deprecated (auto-detection used instead)

---

## Documentation

### Updated Arduino Example

```cpp
// Complete example: 3 sliders, 2 buttons, 1 inline mute
const int SLIDER_PINS[] = {A0, A1, A2};
const int BUTTON_PINS[] = {2, 3};
const int MUTE_PIN = 4;

void setup() {
  Serial.begin(9600);
  pinMode(BUTTON_PINS[0], INPUT_PULLUP);
  pinMode(BUTTON_PINS[1], INPUT_PULLUP);
  pinMode(MUTE_PIN, INPUT_PULLUP);
}

void loop() {
  // Slider 0: Normal
  Serial.print(analogRead(SLIDER_PINS[0]));
  Serial.print('|');

  // Slider 1: Inline mute if button pressed
  if (digitalRead(MUTE_PIN) == LOW) {
    Serial.print("9999");  // Inline mute
  } else {
    Serial.print(analogRead(SLIDER_PINS[1]));
  }
  Serial.print('|');

  // Slider 2: Normal
  Serial.print(analogRead(SLIDER_PINS[2]));
  Serial.print('|');

  // Button 0: Play/Pause (10000 or 10001)
  Serial.print((digitalRead(BUTTON_PINS[0]) == LOW) ? 10001 : 10000);
  Serial.print('|');

  // Button 1: Next Track (10000 or 10001)
  Serial.print((digitalRead(BUTTON_PINS[1]) == LOW) ? 10001 : 10000);

  Serial.println();
  delay(10);
}
```

**Output examples:**
- All off: `512|768|400|10000|10000`
- Button 0 pressed: `512|768|400|10001|10000`
- Slider 1 inline-muted: `512|9999|400|10000|10000`
- Both features: `512|9999|400|10001|10000`

---

## Related Issues

Closes #79 - Serial port prioritization issue (also fixed in this PR with protocol validation and auto-detection)

---

## Future Enhancements

Potential additions for future PRs:
- [ ] Rotary encoder support for volume control
- [ ] LED feedback protocol (send state back to hardware)
- [ ] Long-press button actions (hold for 2s = different action)
- [ ] Button macros (sequence of actions)
- [ ] Configurable inline mute trigger value
- [ ] MIDI controller support using similar protocol

---

## Migration Notes

**Upgrading from button-less versions:**
- No changes needed - everything works as before

**If you had buttons configured with old protocol (0/1 values):**
- Update Arduino code to send `10000` instead of `0`
- Update Arduino code to send `10001` instead of `1`
- Button configurations preserved, just update hardware

**Key Change:**
```cpp
// OLD (ambiguous with slider value 0 and 1)
Serial.print(digitalRead(buttonPin));  // Sends 0 or 1

// NEW (self-documenting, auto-detected)
Serial.print(digitalRead(buttonPin) == LOW ? 10001 : 10000);
```

---

## Benefits Summary

### Before
- ❌ Had to manually configure "number of buttons"
- ❌ Button values (0/1) identical to valid slider values
- ❌ Hard to debug - couldn't tell sliders from buttons in serial data
- ❌ Fragile - changing button count broke configuration

### After
- ✅ Buttons auto-detected - zero configuration
- ✅ Button values (10000/10001) instantly recognizable
- ✅ Self-documenting protocol - debug by reading serial monitor
- ✅ Robust - dynamically adapts to hardware changes
- ✅ Inline mute (9999) for quick hardware muting
- ✅ All special values validated and accepted by protocol
