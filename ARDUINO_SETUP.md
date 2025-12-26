# Настройка Arduino для библиотечного терминала

## Обзор

Система использует Arduino для управления шкафом (открытие/закрытие ячеек, проверка места, звуковые сигналы).

## Файлы проекта

1. **IArduino.cs** - интерфейс для работы с Arduino
2. **ArduinoSerial.cs** - реализация через COM порт (альтернативная)
3. **ArduinoNull.cs** - заглушка для тестирования без железа
4. **ArduinoClientSerial** (в Devices.cs) - основная реализация, используемая в MainForm

## Конфигурация (App.config)

```xml
<!-- Включить/выключить Arduino -->
<add key="EnableArduino" value="true" />

<!-- COM порт Arduino -->
<add key="ArduinoPort" value="COM10" />

<!-- Скорость передачи (обычно 115200 или 9600) -->
<add key="BaudArduino" value="115200" />

<!-- Разделитель строк (обычно \n или \r\n) -->
<add key="NewLineArduino" value="&#10;" />

<!-- Таймауты -->
<add key="ReadTimeoutMs" value="5000" />
<add key="WriteTimeoutMs" value="1000" />
<add key="AutoReconnectMs" value="3000" />
```

## Команды Arduino

### Синхронные команды (с ожиданием ответа)

#### 1. `SPACE?`
**Отправка:** `SPACE?`  
**Ожидаемый ответ:** `SPACE:1` или `SPACE:0` (или `SPACE=1`, `SPACE 1`)  
**Описание:** Проверка наличия свободного места в шкафу  
**Использование:** При возврате книги перед открытием ячейки

**Пример кода Arduino:**
```cpp
if (Serial.available() > 0) {
    String cmd = Serial.readStringUntil('\n');
    cmd.trim();
    
    if (cmd == "SPACE?") {
        bool hasSpace = checkSpace(); // ваша функция проверки
        Serial.println(hasSpace ? "SPACE:1" : "SPACE:0");
    }
}
```

#### 2. `OPENBIN`
**Отправка:** `OPENBIN`  
**Ожидаемый ответ:** `OK` (в течение 10 секунд)  
**Описание:** Открытие ячейки шкафа  
**Использование:** После успешной выдачи или возврата книги

**Пример кода Arduino:**
```cpp
if (cmd == "OPENBIN") {
    openBin(); // ваша функция открытия ячейки
    delay(100); // небольшая задержка на механику
    Serial.println("OK");
}
```

### Асинхронные команды (без ожидания ответа)

#### 3. `OK`
**Отправка:** `OK`  
**Описание:** Сигнал успешной операции (опционально)  
**Использование:** После успешной выдачи/возврата книги

#### 4. `ERR`
**Отправка:** `ERR`  
**Описание:** Сигнал ошибки (опционально)  
**Использование:** При ошибках (неверная карта, книга не найдена и т.д.)

#### 5. `BEEP:120`
**Отправка:** `BEEP:120` (где 120 - длительность в миллисекундах)  
**Описание:** Звуковой сигнал  
**Использование:** После успешных операций

### Бинарные команды управления шкафом (8 байт)

Система поддерживает бинарный протокол для адресного управления ячейками шкафа. Формат пакета: `FF, CMD, fromX, fromY, toX, toY, sizeX, sizeY` (8 байт).

#### Команды управления:

1. **`INIT` (0x00)** - Инициализация шкафа
   ```csharp
   _ardu.Init(fromX, fromY, toX, toY, sizeX, sizeY);
   ```

2. **`GIVEFRONT` (0x02)** - Выдача книги на переднюю сторону
   ```csharp
   _ardu.GiveFront(fromX, fromY, toX, toY, sizeX, sizeY);
   ```

3. **`TAKEFRONT` (0x03)** - Взятие книги с передней стороны
   ```csharp
   _ardu.TakeFront(fromX, fromY, toX, toY, sizeX, sizeY);
   ```

4. **`GIVEBACK` (0x04)** - Выдача книги на заднюю сторону
   ```csharp
   _ardu.GiveBack(fromX, fromY, toX, toY, sizeX, sizeY);
   ```

5. **`TAKEBACK` (0x05)** - Взятие книги с задней стороны
   ```csharp
   _ardu.TakeBack(fromX, fromY, toX, toY, sizeX, sizeY);
   ```

**Параметры:**
- `fromX`, `fromY` - координаты начальной позиции (0-254)
- `toX`, `toY` - координаты целевой позиции (0-254)
- `sizeX`, `sizeY` - размеры сетки ячеек (например, 3x22)

**Пример использования:**
```csharp
// Команда TAKEFRONT из control-fulltest.bat:
// control COM8 TAKEFRONT 1 9 0 0 3 22
_ardu.TakeFront(fromX: 1, fromY: 9, toX: 0, toY: 0, sizeX: 3, sizeY: 22);

// Команда GIVEFRONT:
// control COM8 GIVEFRONT 0 0 1 9 3 22
_ardu.GiveFront(fromX: 0, fromY: 0, toX: 1, toY: 9, sizeX: 3, sizeY: 22);
```

**Пример обработки на Arduino:**
```cpp
void loop() {
    if (Serial.available() >= 8) {
        byte packet[8];
        Serial.readBytes(packet, 8);
        
        if (packet[0] == 0xFF) {  // Проверка стартового байта
            byte cmd = packet[1];
            byte fromX = packet[2];
            byte fromY = packet[3];
            byte toX = packet[4];
            byte toY = packet[5];
            byte sizeX = packet[6];
            byte sizeY = packet[7];
            
            switch (cmd) {
                case 0x00: // INIT
                    initializeCabinet(fromX, fromY, toX, toY, sizeX, sizeY);
                    break;
                case 0x02: // GIVEFRONT
                    giveFront(fromX, fromY, toX, toY, sizeX, sizeY);
                    break;
                case 0x03: // TAKEFRONT
                    takeFront(fromX, fromY, toX, toY, sizeX, sizeY);
                    break;
                case 0x04: // GIVEBACK
                    giveBack(fromX, fromY, toX, toY, sizeX, sizeY);
                    break;
                case 0x05: // TAKEBACK
                    takeBack(fromX, fromY, toX, toY, sizeX, sizeY);
                    break;
            }
        }
    }
}
```

## Пример полного кода Arduino

```cpp
void setup() {
    Serial.begin(115200);
    // Инициализация пинов для управления шкафом
    pinMode(LED_BUILTIN, OUTPUT);
    // ... другие настройки
}

void loop() {
    if (Serial.available() > 0) {
        String cmd = Serial.readStringUntil('\n');
        cmd.trim();
        
        // Синхронные команды
        if (cmd == "SPACE?") {
            bool hasSpace = checkSpace();
            Serial.println(hasSpace ? "SPACE:1" : "SPACE:0");
        }
        else if (cmd == "OPENBIN") {
            openBin();
            delay(100);
            Serial.println("OK");
        }
        // Асинхронные команды (можно игнорировать или логировать)
        else if (cmd == "OK") {
            // Сигнал успеха (опционально)
        }
        else if (cmd == "ERR") {
            // Сигнал ошибки (опционально)
        }
        else if (cmd.startsWith("BEEP:")) {
            int duration = cmd.substring(5).toInt();
            beep(duration);
        }
    }
}

bool checkSpace() {
    // Ваша логика проверки места
    // Например, проверка датчика или счетчика
    return true; // или false
}

void openBin() {
    // Ваша логика открытия ячейки
    // Например, активация сервопривода или реле
    digitalWrite(LED_BUILTIN, HIGH);
    delay(2000); // время открытия
    digitalWrite(LED_BUILTIN, LOW);
}

void beep(int duration) {
    // Ваша логика звукового сигнала
    // Например, tone(pin, frequency, duration)
    tone(8, 1000, duration); // пин 8, частота 1000 Гц
}
```

## Логирование

Все команды и ответы логируются в файл `Logs/arduino.log`:
- `>>` - отправленные команды
- `<<` - полученные ответы
- `OPENED` / `CLOSED` - события подключения

## Отладка

1. **Проверка подключения:**
   - Убедитесь, что COM порт указан правильно в `App.config`
   - Проверьте скорость передачи (должна совпадать с Arduino)
   - Проверьте разделитель строк (`\n` или `\r\n`)

2. **Проверка команд:**
   - Откройте `Logs/arduino.log` для просмотра всех команд
   - Используйте Serial Monitor в Arduino IDE для отладки

3. **Тестирование без железа:**
   - Установите `EnableArduino` в `false` - команды будут только логироваться
   - Или используйте `ArduinoNull` класс для эмуляции

## Важные замечания

1. **Таймауты:**
   - `SPACE?` должен ответить в течение `ReadTimeoutMs` (по умолчанию 5 секунд)
   - `OPENBIN` должен ответить в течение `ReadTimeoutMs + 10000` (15 секунд)

2. **Формат ответов:**
   - Ответы должны заканчиваться символом новой строки (`\n` или `\r\n`)
   - Для `SPACE?` система принимает варианты: `SPACE:1`, `SPACE=1`, `SPACE 1`, `1`
   - Для `OPENBIN` система ожидает точно `OK`

3. **Автопереподключение:**
   - При потере связи система автоматически переподключится через `AutoReconnectMs` (3 секунды)

## Пример настройки для разных Arduino

### Arduino Uno/Nano (9600 бод)
```xml
<add key="BaudArduino" value="9600" />
<add key="ArduinoPort" value="COM3" />
```

### Arduino Mega/ESP32 (115200 бод)
```xml
<add key="BaudArduino" value="115200" />
<add key="ArduinoPort" value="COM10" />
```

### Автоопределение порта
Используйте `PortResolver` с префиксом `auto:`:
```xml
<add key="ArduinoPort" value="auto:VID_2341&PID_0043" />
```

