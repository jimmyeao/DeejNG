// ==================== SLIDER CONFIGURATION ====================
const int NUM_SLIDERS = 5;
const int analogInputs[NUM_SLIDERS] = {A3, A2, A1, A0, 10};
int analogSliderValues[NUM_SLIDERS];

// ==================== BUTTON CONFIGURATION ====================
const int NUM_BUTTONS = 8;
const int buttonPins[NUM_BUTTONS] = {2, 3, 4, 5, 6, 14, 15, 16};

// Protocol values
const int BUTTON_NONE  = 10000; // no event
const int BUTTON_SHORT = 10001; // short press event (on release)
const int BUTTON_LONG  = 11111; // long press event (on release)

// Debounce + press timing
const unsigned long DEBOUNCE_DELAY_MS = 20;   // 0 is risky; 20 is typical
const unsigned long LONG_PRESS_MS     = 600;  // adjust to taste

// ==================== BUTTON STATE ====================
// We send these every frame (like before), but now they're "events" (pulses)
int buttonStates[NUM_BUTTONS];

// Raw reading & debounce tracking
int lastButtonReading[NUM_BUTTONS];
unsigned long lastDebounceTime[NUM_BUTTONS];

// Stable debounced pressed state
bool stablePressed[NUM_BUTTONS];

// Press timing
unsigned long pressStartTime[NUM_BUTTONS];

// "Event pending" flag so we can clear the pulse after 1 send
bool eventPending[NUM_BUTTONS];

void setup() {
  for (int i = 0; i < NUM_SLIDERS; i++) {
    pinMode(analogInputs[i], INPUT);
  }

  for (int i = 0; i < NUM_BUTTONS; i++) {
    pinMode(buttonPins[i], INPUT_PULLUP);

    buttonStates[i] = BUTTON_NONE;
    lastButtonReading[i] = HIGH;
    lastDebounceTime[i] = 0;

    stablePressed[i] = false;
    pressStartTime[i] = 0;

    eventPending[i] = false;
  }

  Serial.begin(9600);
}

void loop() {
  updateSliderValues();
  updateButtonStates();

  sendSliderValues();

  // Clear any one-frame events after we've sent them once
  clearButtonEvents();

  delay(10);
}

void updateSliderValues() {
  for (int i = 0; i < NUM_SLIDERS; i++) {
    analogSliderValues[i] = analogRead(analogInputs[i]);
  }
}

void updateButtonStates() {
  unsigned long now = millis();

  for (int i = 0; i < NUM_BUTTONS; i++) {
    int reading = digitalRead(buttonPins[i]);

    // Debounce: track last change time
    if (reading != lastButtonReading[i]) {
      lastDebounceTime[i] = now;
    }

    // If stable long enough, accept as debounced
    if ((now - lastDebounceTime[i]) >= DEBOUNCE_DELAY_MS) {
      bool isPressed = (reading == LOW);

      // Detect stable edge transitions
      if (isPressed && !stablePressed[i]) {
        // Press started
        stablePressed[i] = true;
        pressStartTime[i] = now;
      }
      else if (!isPressed && stablePressed[i]) {
        // Released: compute duration and emit event
        stablePressed[i] = false;

        unsigned long heldMs = now - pressStartTime[i];

        buttonStates[i] = (heldMs >= LONG_PRESS_MS) ? BUTTON_LONG : BUTTON_SHORT;
        eventPending[i] = true;
      }

      // While held: we send no event (BUTTON_NONE) continuously
      // buttonStates[i] remains BUTTON_NONE unless we set an event on release
    }

    lastButtonReading[i] = reading;
  }
}

void clearButtonEvents() {
  for (int i = 0; i < NUM_BUTTONS; i++) {
    if (eventPending[i]) {
      // We already sent the event in sendSliderValues() once this loop,
      // so reset it back to NONE for the next frame.
      buttonStates[i] = BUTTON_NONE;
      eventPending[i] = false;
    }
  }
}

void sendSliderValues() {
  String builtString = "";

  // Sliders
  for (int i = 0; i < NUM_SLIDERS; i++) {
    builtString += String((int)analogSliderValues[i]);
    builtString += "|";
  }

  // Buttons (events)
  for (int i = 0; i < NUM_BUTTONS; i++) {
    builtString += String(buttonStates[i]);
    if (i < NUM_BUTTONS - 1) builtString += "|";
  }

  Serial.println(builtString);
}