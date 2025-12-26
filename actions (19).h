#include "config.h"

//#define TRAY 0
#define A 0
#define B 1

#define X 0
#define Y 1

#define enginesABDefined() (defined(enginesAStepPIN) && defined(enginesADirPIN) && defined(enginesBStepPIN) && defined(enginesBDirPIN))
#define enginesTrayDefined() (defined(enginesTrayStepPIN) && defined(enginesTrayDirPIN))
/*
#define enginesDefined(TYPE) (defined(engines##TYPE##StepPIN) && defined(engines##TYPE##DirPIN))
#define enginesDefinedAND(TYPE, TYPE2) (defined(engines##TYPE##StepPIN) && defined(engines##TYPE##DirPIN) && defined(engines##TYPE2##StepPIN) && defined(engines##TYPE2##DirPIN))
#define enginesDefinedOR(TYPE, TYPE2) ((defined(engines##TYPE##StepPIN) && defined(engines##TYPE##DirPIN)) || (defined(engines##TYPE2##StepPIN) && defined(engines##TYPE2##DirPIN)))
*/

#if (defined(enginesTrayStepPIN) && defined(enginesTrayDirPIN)) || (defined(enginesAStepPIN) && defined(enginesADirPIN)) || (defined(enginesBStepPIN) && defined(enginesBDirPIN))
#include <AccelStepper.h>

#if defined(enginesTrayStepPIN) && defined(enginesTrayDirPIN)
AccelStepper *stepperTray;
#endif
#if defined(enginesAStepPIN) && defined(enginesADirPIN)
AccelStepper *stepperA;
#endif
#if defined(enginesBStepPIN) && defined(enginesBDirPIN)
AccelStepper *stepperB;
#endif

#define PATH_MAX_POSITIVE 2147483647
#define PATH_MAX_NEGATIVE -2147483647

unsigned long steppersTraySize;
unsigned long fieldSizes[] = {0, 0};
long currentFieldPos[] = {0, 0};

long steppersTask[] = {0, 0, 0, 0, 0, 0}; //fromX fromY toX toY sizeX sizeY
#define STEPPERSTASK_FROM_X 0
#define STEPPERSTASK_FROM_Y 1
#define STEPPERSTASK_TO_X 2
#define STEPPERSTASK_TO_Y 3
#define STEPPERSTASK_WIDTH 4
#define STEPPERSTASK_HEIGHT 5
//#define STEPPERSTASK_SIZE 6

#include <MultiStepper.h>

#if enginesABDefined()
MultiStepper *steppersAB;
unsigned long steppersTargetXY[2];
long steppersTargetAB[2];
#endif

#if enginesTrayDefined()
MultiStepper *steppersTray;
unsigned long steppersTargetTray[1];
#endif
#endif

#if (sensorsTrayBeginDebouncing != 0) || (sensorsTrayEndDebouncing != 0) || (sensorsXBeginDebouncing != 0) || (sensorsXEndDebouncing != 0) || (sensorsYBeginDebouncing != 0) || (sensorsYEndDebouncing != 0)
#include <Bounce2.h>

#if sensorsTrayBeginDebouncing != 0
Bounce2::Button *debouncerSensorsTrayBegin;
#endif
#if sensorsTrayEndDebouncing != 0
Bounce2::Button *debouncerSensorsTrayEnd;
#endif

#if sensorsXBeginDebouncing != 0
Bounce2::Button *debouncerSensorsXBegin;
#endif
#if sensorsXEndDebouncing != 0
Bounce2::Button *debouncerSensorsXEnd;
#endif

#if sensorsYBeginDebouncing != 0
Bounce2::Button *debouncerSensorsYBegin;
#endif
#if sensorsYEndDebouncing != 0
Bounce2::Button *debouncerSensorsYEnd;
#endif
#endif

#ifdef doorsOutSidePIN
unsigned long doorsOutSideActionedTime;
#endif
#ifdef doorsInSidePIN
unsigned long doorsInSideActionedTime;
#endif

#if defined(servosLock1PIN) || defined(servosLock2PIN)
#include <Servo.h>

#ifdef servosLock1PIN
Servo *servosLock1;
unsigned long servosLock1ActionedTime;
#endif
#ifdef servosLock2PIN
Servo *servosLock2;
unsigned long servosLock2ActionedTime;
#endif
#endif

#if enginesTrayDefined()
/*
bool enginesTrayEnable() {
  stepperTray->enableOutputs();

  return true;
}
bool enginesTrayDisable() {
  stepperTray->disableOutputs();

  return true;
}
*/
bool enginesTraySetZeroPos() {
  stepperTray->setCurrentPosition(0);

  return true;
}

bool enginesTrayMoved() {
/*
#ifdef enginesTrayAccel
  return !stepperTray->run();
#else
  return !stepperTray->runSpeed();
#endif
*/
  return !steppersTray->run();
}

bool enginesTrayMoveToBeginOut() {
  //stepperTray->moveTo(PATH_MAX_NEGATIVE);
  steppersTargetTray[0] = PATH_MAX_NEGATIVE;
  steppersTray->moveTo(steppersTargetTray);

  return true;
}
bool enginesTrayMoveToEndOut() {
  //stepperTray->moveTo(PATH_MAX_POSITIVE);
  steppersTargetTray[0] = PATH_MAX_POSITIVE;
  steppersTray->moveTo(steppersTargetTray);

  return true;
}
bool enginesTrayMoveToBegin() {
  //stepperTray->moveTo(0);
  steppersTargetTray[0] = 0;
  steppersTray->moveTo(steppersTargetTray);

  return true;
}
bool enginesTrayMoveToEnd() {
  //stepperTray->moveTo(steppersTraySize - 1);
  steppersTargetTray[0] = steppersTraySize - 1;
  steppersTray->moveTo(steppersTargetTray);

  return true;
}
bool enginesTrayMoveToBase() {
  //stepperTray->moveTo(steppersTraySize * enginesTrayBasePos - 1);
  steppersTargetTray[0] = steppersTraySize * enginesTrayBasePos - 1;
  steppersTray->moveTo(steppersTargetTray);

  return true;
}
bool enginesTrayMoveToFront() {
  //stepperTray->moveTo(steppersTraySize * enginesTrayFrontPos - 1);
  steppersTargetTray[0] = steppersTraySize * enginesTrayFrontPos - 1;
  steppersTray->moveTo(steppersTargetTray);

  return true;
}
bool enginesTrayMoveToBack() {
  //stepperTray->moveTo(steppersTraySize * enginesTrayBackPos - 1);
  steppersTargetTray[0] = steppersTraySize * enginesTrayBackPos - 1;
  steppersTray->moveTo(steppersTargetTray);

  return true;
}

bool sensorsTrayBeginCheck() {
  enginesTrayMoved();

#if sensorsTrayBeginDebouncing == 0
  return (digitalRead(sensorsTrayBeginPIN) == sensorsTrayBeginTrigger);
#else
  debouncerSensorsTrayBegin->update();
  return debouncerSensorsTrayBegin->pressed();
#endif
}
bool sensorsTrayEndCheck() {
  enginesTrayMoved();

#if sensorsTrayEndDebouncing == 0
  return (digitalRead(sensorsTrayEndPIN) == sensorsTrayEndTrigger);
#else
  debouncerSensorsTrayEnd->update();
  return debouncerSensorsTrayEnd->pressed();
#endif
}

bool enginesTraySaveSize() {
  steppersTraySize = stepperTray->currentPosition() + 1;

  return true;
}
#endif

#if enginesABDefined()
void patchXYModifiers(unsigned long coords[2]) {
  /*
  coords[X] = (coords[X] * coordsXSizeModifier) + (fieldSizes[X] * coordsXBeginModifier);
  coords[Y] = (coords[Y] * coordsXSizeModifier) + (fieldSizes[Y] * coordsXBeginModifier);
  */
//#if coordsXSizeModifier < 0
  coords[X] = (coords[X] * coordsXSizeModifier);
//#else
//  coords[X] = coords[X] + coordsXSizeModifier;
//#endif
//#if coordsYSizeModifier < 0
  coords[Y] = (coords[Y] * coordsYSizeModifier);
//#else
//  coords[Y] = coords[Y] + coordsYSizeModifier;
//#endif

//#if coordsXBeginModifier < 0
  coords[X] = coords[X] + (fieldSizes[X] * coordsXBeginModifier);
//#else
//  coords[X] = coords[X] + coordsXBeginModifier;
//#endif
//#if coordsYBeginModifier < 0
  coords[Y] = coords[Y] + (fieldSizes[Y] * coordsYBeginModifier);
//#else
//  coords[Y] = coords[Y] + coordsYBeginModifier;
//#endif
}
/*
bool enginesABEnable() {
  stepperA->enableOutputs();
  stepperB->enableOutputs();

  return true;
}
bool enginesABDisable() {
  stepperA->disableOutputs();
  stepperB->disableOutputs();

  return true;
}
*/
bool enginesABSetSpeed() {
  stepperA->setMaxSpeed(enginesABSpeed);
  stepperB->setMaxSpeed(enginesABSpeed);
  stepperA->setSpeed(enginesABSpeed);
  stepperB->setSpeed(enginesABSpeed);

  return true;
}

bool enginesABSetZeroPos() {
  stepperA->setCurrentPosition(0);
  stepperB->setCurrentPosition(0);

  return true;
}

bool enginesABMoved() {
  return !steppersAB->run();
}

bool enginesAMoveToBeginOut() {
  steppersTargetAB[A] = PATH_MAX_NEGATIVE;
  steppersTargetAB[B] = 0;

  steppersAB->moveTo(steppersTargetAB);

  return true;
}
bool enginesBMoveToBeginOut() {
  steppersTargetAB[A] = 0;
  steppersTargetAB[B] = PATH_MAX_NEGATIVE;

  steppersAB->moveTo(steppersTargetAB);

  return true;
}
bool enginesABMoveToBeginOut() {
  //steppersTargetAB[A] = stepperA->targetPosition();

#if sensorsYEndDebouncing == 0
  if (digitalRead(sensorsYEndPIN) == sensorsYEndTrigger)
#else
  debouncerSensorsYEnd->update();
  if (debouncerSensorsYEnd->pressed())
#endif
    steppersTargetAB[B] = stepperA->targetPosition();
  else
    steppersTargetAB[B] = -(stepperA->targetPosition());
  
  steppersAB->moveTo(steppersTargetAB);

  return true;
}

bool enginesAMoveToEndOut() {
  steppersTargetAB[A] = PATH_MAX_POSITIVE;
  steppersTargetAB[B] = 0;

  steppersAB->moveTo(steppersTargetAB);

  return true;
}
bool enginesBMoveToEndOut() {
  steppersTargetAB[A] = 0;
  steppersTargetAB[B] = PATH_MAX_POSITIVE;

  steppersAB->moveTo(steppersTargetAB);

  return true;
}
bool enginesABMoveToEndOut() {
  //steppersTargetAB[A] = stepperA->targetPosition();

#if sensorsYBeginDebouncing == 0
  if (digitalRead(sensorsYBeginPIN) == sensorsYBeginTrigger)
#else
  debouncerSensorsYBegin->update();
  if (debouncerSensorsYBegin->pressed())
#endif
    steppersTargetAB[B] = stepperA->targetPosition();
  else
    steppersTargetAB[B] = -(stepperA->targetPosition());
  
  steppersAB->moveTo(steppersTargetAB);

  return true;
}

#define moveABSteppersXY(TARGET_XY, CURRENT_XY, ENGINES, TARGET_AB)\
enginesABSetZeroPos();\
TARGET_AB[A] = (TARGET_XY[X] - CURRENT_XY[X]) + (TARGET_XY[Y] - CURRENT_XY[Y]);\
TARGET_AB[B] = ((TARGET_XY[X] - CURRENT_XY[X]) * 2) - TARGET_AB[A];\
ENGINES->moveTo(TARGET_AB);\
CURRENT_XY[X] = TARGET_XY[X];\
CURRENT_XY[Y] = TARGET_XY[Y];

bool enginesABMoveToTaskFrom() {
  steppersTargetXY[X] = steppersTask[STEPPERSTASK_FROM_X] * (fieldSizes[X] / (steppersTask[STEPPERSTASK_WIDTH] - 1));
  steppersTargetXY[Y] = steppersTask[STEPPERSTASK_FROM_Y] * (fieldSizes[Y] / (steppersTask[STEPPERSTASK_HEIGHT] - 1));

  Serial.print(steppersTargetXY[X]);
  Serial.print(":");
  Serial.print(steppersTargetXY[Y]);
  Serial.print(" => ");
  
  patchXYModifiers(steppersTargetXY);
  Serial.print(steppersTargetXY[X]);
  Serial.print(":");
  Serial.println(steppersTargetXY[Y]);

  moveABSteppersXY(steppersTargetXY, currentFieldPos, steppersAB, steppersTargetAB);

  return true;
}
bool enginesABMoveToTaskTo() {
  steppersTargetXY[X] = steppersTask[STEPPERSTASK_TO_X] * (fieldSizes[X] / (steppersTask[STEPPERSTASK_WIDTH] - 1));
  steppersTargetXY[Y] = steppersTask[STEPPERSTASK_TO_Y] * (fieldSizes[Y] / (steppersTask[STEPPERSTASK_HEIGHT] - 1));

  Serial.print(steppersTargetXY[X]);
  Serial.print(":");
  Serial.print(steppersTargetXY[Y]);
  Serial.print(" => ");
  
  patchXYModifiers(steppersTargetXY);
  Serial.print(steppersTargetXY[X]);
  Serial.print(":");
  Serial.println(steppersTargetXY[Y]);

  moveABSteppersXY(steppersTargetXY, currentFieldPos, steppersAB, steppersTargetAB);

  return true;
}
bool enginesABMoveToBase() {
  steppersTargetXY[X] = fieldSizes[X] / 2 - 1;
  steppersTargetXY[Y] = fieldSizes[Y] / 2 - 1;

  moveABSteppersXY(steppersTargetXY, currentFieldPos, steppersAB, steppersTargetAB);

  return true;
}

bool sensorsXBeginORYEndCheck() {
  enginesABMoved();

#if (sensorsXBeginDebouncing == 0) && (sensorsYEndDebouncing == 0)
  return ((digitalRead(sensorsXBeginPIN) == sensorsXBeginTrigger) || (digitalRead(sensorsYEndPIN) == sensorsYEndTrigger));
#else
  debouncerSensorsXBegin->update();
  debouncerSensorsYEnd->update();
  return (debouncerSensorsXBegin->pressed() || debouncerSensorsYEnd->pressed());
#endif
}
bool sensorsXBeginANDYEndCheck() {
  enginesABMoved();
  
#if (sensorsXBeginDebouncing == 0) && (sensorsYEndDebouncing == 0)
  return ((digitalRead(sensorsXBeginPIN) == sensorsXBeginTrigger) && (digitalRead(sensorsYEndPIN) == sensorsYEndTrigger));
#else
  debouncerSensorsXBegin->update();
  debouncerSensorsYEnd->update();
  return (debouncerSensorsXBegin->pressed() && debouncerSensorsYEnd->pressed());
#endif
}

bool sensorsXEndORYBeginCheck() {
  enginesABMoved();
  
#if (sensorsXEndDebouncing == 0) && (sensorsYBeginDebouncing == 0)
  return ((digitalRead(sensorsXEndPIN) == sensorsXEndTrigger) || (digitalRead(sensorsYBeginPIN) == sensorsYBeginTrigger));
#else
  debouncerSensorsXEnd->update();
  debouncerSensorsYBegin->update();
  return (debouncerSensorsXEnd->pressed() || debouncerSensorsYBegin->pressed());
#endif
}
bool sensorsXEndANDYBeginCheck() {
  enginesABMoved();
  
#if (sensorsXEndDebouncing == 0) && (sensorsYBeginDebouncing == 0)
  return ((digitalRead(sensorsXEndPIN) == sensorsXEndTrigger) && (digitalRead(sensorsYBeginPIN) == sensorsYBeginTrigger));
#else
  debouncerSensorsXEnd->update();
  debouncerSensorsYBegin->update();
  return (debouncerSensorsXEnd->pressed() && debouncerSensorsYBegin->pressed());
#endif
}

bool enginesABSaveSize() {
  currentFieldPos[X] = (stepperA->currentPosition() + stepperB->currentPosition()) / 2;
  currentFieldPos[Y] = (stepperA->currentPosition() - stepperB->currentPosition()) / 2;

  fieldSizes[X] = currentFieldPos[X] + 1;
  fieldSizes[Y] = currentFieldPos[Y] + 1;

  Serial.print("width: ");
  Serial.print(fieldSizes[X]);
  Serial.print(" height: ");
  Serial.println(fieldSizes[Y]);

  return true;
}

bool fastinitABSetSize() {
  fieldSizes[X] = fastinitABWidth;
  fieldSizes[Y] = fastinitABHeight;

  return true;
}
#endif
////////
#ifdef doorsOutSidePIN
bool doorsOutSideOpen() {
  digitalWrite(doorsOutSidePIN, doorsOutSideOpenValue);

  doorsOutSideActionedTime = millis() + doorsOutSideActionDelay;
  return true;
}
bool doorsOutSideClose() {
  digitalWrite(doorsOutSidePIN, doorsOutSideCloseValue);

  doorsOutSideActionedTime = millis() + doorsOutSideActionDelay;
  return true;
}
bool doorsOutSideActioned() {
  return (millis() > doorsOutSideActionedTime);
}
#endif

#ifdef doorsInSidePIN
bool doorsInSideOpen() {
  digitalWrite(doorsInSidePIN, doorsInSideOpenValue);

  doorsInSideActionedTime = millis() + doorsInSideActionDelay;
  return true;
}
bool doorsInSideClose() {
  digitalWrite(doorsInSidePIN, doorsInSideCloseValue);

  doorsInSideActionedTime = millis() + doorsInSideActionDelay;
  return true;
}
bool doorsInSideActioned() {
  return (millis() > doorsInSideActionedTime);
}
#endif
////////
#ifdef servosLock1PIN
bool servosLock1Open() {
  servosLock1->write(servosLock1OpenedValue);

  servosLock1ActionedTime = millis() + servosLock1ActionDelay;
  return true;
}
bool servosLock1Close() {
  servosLock1->write(servosLock1ClosedValue);

  servosLock1ActionedTime = millis() + servosLock1ActionDelay;
  return true;
}
bool servosLock1Actioned() {
  return (millis() > servosLock1ActionedTime);
}
#endif

#ifdef servosLock2PIN
bool servosLock2Open() {
  servosLock2->write(servosLock2OpenedValue);

  servosLock2ActionedTime = millis() + servosLock2ActionDelay;
  return true;
}
bool servosLock2Close() {
  servosLock2->write(servosLock2ClosedValue);

  servosLock2ActionedTime = millis() + servosLock2ActionDelay;
  return true;
}
bool servosLock2Actioned() {
  return (millis() > servosLock2ActionedTime);
}
#endif
/*///////
#ifdef sensorHandPIN
bool sensorHandCheck() {
  return (digitalRead(sensorHandPIN) == sensorHandEmptyValue);
}
#endif
///////*/
void initialize() {
  delay(4000);

#if enginesTrayDefined()
  pinMode(enginesTrayStepPIN, OUTPUT);
  pinMode(enginesTrayDirPIN, OUTPUT);

  stepperTray = new AccelStepper(AccelStepper::DRIVER, enginesTrayStepPIN, enginesTrayDirPIN);
  stepperTray->setMaxSpeed(enginesTraySpeed);
#ifdef enginesTrayAccel
  stepperTray->setAcceleration(enginesTrayAccel);
#else
  stepperTray->setSpeed(enginesTraySpeed);
  
  steppersTray = new MultiStepper();
  steppersTray->addStepper(*stepperTray);
#endif
#endif
/*
#ifdef sensorsTrayBeginPIN
  pinMode(sensorsTrayBeginPIN, INPUT);
#endif
#ifdef sensorsTrayEndPIN
  pinMode(sensorsTrayEndPIN, INPUT);
#endif
*/

#if defined(enginesAStepPIN) && defined(enginesADirPIN)
  pinMode(enginesAStepPIN, OUTPUT);
  pinMode(enginesADirPIN, OUTPUT);

  stepperA = new AccelStepper(AccelStepper::DRIVER, enginesAStepPIN, enginesADirPIN);
  stepperA->setMaxSpeed(enginesABInitSpeed);
  stepperA->setSpeed(enginesABInitSpeed);
#endif
#if defined(enginesBStepPIN) && defined(enginesBDirPIN)
  pinMode(enginesBStepPIN, OUTPUT);
  pinMode(enginesBDirPIN, OUTPUT);

  stepperB = new AccelStepper(AccelStepper::DRIVER, enginesBStepPIN, enginesBDirPIN);
  stepperB->setMaxSpeed(enginesABInitSpeed);
  stepperB->setSpeed(enginesABInitSpeed);
#endif
#if enginesABDefined()
  steppersAB = new MultiStepper();
  steppersAB->addStepper(*stepperA);
  steppersAB->addStepper(*stepperB);
#endif

/*
#ifdef sensorsXBeginPIN
  pinMode(sensorsXBeginPIN, INPUT);
#endif
#ifdef sensorsXEndPIN
  pinMode(sensorsXEndPIN, INPUT);
#endif
#ifdef sensorsYBeginPIN
  pinMode(sensorsYBeginPIN, INPUT);
#endif
#ifdef sensorsYEndPIN
  pinMode(sensorsYEndPIN, INPUT);
#endif
*/
#if sensorsTrayBeginDebouncing != 0
  debouncerSensorsTrayBegin = new Bounce2::Button();
  debouncerSensorsTrayBegin->attach(sensorsTrayBeginPIN);
  debouncerSensorsTrayBegin->interval(sensorsTrayBeginDebouncing);
  debouncerSensorsTrayBegin->setPressedState(sensorsTrayBeginTrigger);
#endif
#if sensorsTrayEndDebouncing != 0
  debouncerSensorsTrayEnd = new Bounce2::Button();
  debouncerSensorsTrayEnd->attach(sensorsTrayEndPIN);
  debouncerSensorsTrayEnd->interval(sensorsTrayEndDebouncing);
  debouncerSensorsTrayEnd->setPressedState(sensorsTrayEndTrigger);
#endif

#if sensorsXBeginDebouncing != 0
  debouncerSensorsXBegin = new Bounce2::Button();
  debouncerSensorsXBegin->attach(sensorsXBeginPIN);
  debouncerSensorsXBegin->interval(sensorsXBeginDebouncing);
  debouncerSensorsXBegin->setPressedState(sensorsXBeginTrigger);
#endif
#if sensorsXEndDebouncing != 0
  debouncerSensorsXEnd = new Bounce2::Button();
  debouncerSensorsXEnd->attach(sensorsXEndPIN);
  debouncerSensorsXEnd->interval(sensorsXEndDebouncing);
  debouncerSensorsXEnd->setPressedState(sensorsXEndTrigger);
#endif

#if sensorsYBeginDebouncing != 0
  debouncerSensorsYBegin = new Bounce2::Button();
  debouncerSensorsYBegin->attach(sensorsYBeginPIN);
  debouncerSensorsYBegin->interval(sensorsYBeginDebouncing);
  debouncerSensorsYBegin->setPressedState(sensorsYBeginTrigger);
#endif
#if sensorsYEndDebouncing != 0
  debouncerSensorsYEnd = new Bounce2::Button();
  debouncerSensorsYEnd->attach(sensorsYEndPIN);
  debouncerSensorsYEnd->interval(sensorsYEndDebouncing);
  debouncerSensorsYEnd->setPressedState(sensorsYEndTrigger);
#endif
  ////////
#ifdef doorsOutSidePIN
  pinMode(doorsOutSidePIN, OUTPUT);
  doorsOutSideClose();
#endif
#ifdef doorsInSidePIN
  pinMode(doorsInSidePIN, OUTPUT);
  doorsInSideClose();
#endif
////////
#ifdef servosLock1PIN
  servosLock1 = new Servo();
  servosLock1->attach(servosLock1PIN);
  servosLock1Open();
#endif
#ifdef servosLock2PIN
  servosLock2 = new Servo();
  servosLock2->attach(servosLock2PIN);
  servosLock2Open();
#endif

  delay(4000);
}

bool testData() {
  steppersTask[0] = 0; // fromX
  steppersTask[1] = 0; // fromY

  steppersTask[2] = 2; //toX
  steppersTask[3] = 21; //toY

  steppersTask[4] = 3; // width
  steppersTask[5] = 22; // height

  return true;
}