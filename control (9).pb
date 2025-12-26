EnableExplicit

If CountProgramParameters() <> 8
  PrintN("Usage:" + #CRLF$ +
         "control COM? COMMAND fromX fromY toX toY sizeX sizeY" + #CRLF$ +
         #CRLF$ +
         "Where COMMAND is one of (INIT, GIVEFRONT, TAKEFRONT, GIVEBACK, TAKEBACK)" + #CRLF$ +
         "Where fromX/fromY/toX/toY is digit 0-254 of coord-positions" + #CRLF$ +
         "Where sizeX/sizeY is digit 0-254 of fields-amount" + #CRLF$)
  
  End
EndIf

PrintN("Params: ")
Define iParam.u
Dim Params.s(7)
For iParam = 0 To 7
  Params(iParam) = ProgramParameter(iParam)
  
  PrintN(Str(iParam) + "=" + Params(iParam))
Next

OpenConsole()

#SERIALPORT = 0
#BAUDRATE = 115200

If Not OpenSerialPort(#SERIALPORT, Params(0), #BAUDRATE, #PB_SerialPort_NoParity, 8, 1, #PB_SerialPort_NoHandshake, 8, 8)
  PrintN("Can't open serial port " + Params(0))
  
  End
EndIf

Print("Packet: ")

Define *DataPacket = AllocateMemory(8, #PB_Memory_NoClear)
PokeB(*DataPacket + 0, $FF) ;Reset
Print(" FF")

Select Params(1)
  Case "INIT"
    PokeB(*DataPacket + 1, $00) ;Command
    Print(" " + Str($00))
    
  ;Case "RESERVED"
  ;  PokeB(*DataPacket + 1, $01) ;Command
  ;  Print(" " + Str($01))
    
  Case "GIVEFRONT"
    PokeB(*DataPacket + 1, $02) ;Command
    Print(" " + Str($02))
    
  Case "TAKEFRONT"
    PokeB(*DataPacket + 1, $03) ;Command
    Print(" " + Str($03))
    
  Case "GIVEFRONT"
    PokeB(*DataPacket + 1, $04) ;Command
    Print(" " + Str($04))
    
  Case "TAKEFRONT"
    PokeB(*DataPacket + 1, $05) ;Command
    Print(" " + Str($05))
    
EndSelect

PokeB(*DataPacket + 2, Val(Params(2))) ;From X
PokeB(*DataPacket + 3, Val(Params(3))) ;From Y

Print(" " + Str(Val(Params(2))))
Print(" " + Str(Val(Params(3))))

PokeB(*DataPacket + 4, Val(Params(4))) ;To X
PokeB(*DataPacket + 5, Val(Params(5))) ;To Y

Print(" " + Str(Val(Params(4))))
Print(" " + Str(Val(Params(5))))

PokeB(*DataPacket + 6, Val(Params(6))) ;Size X
PokeB(*DataPacket + 7, Val(Params(7))) ;Size Y

Print(" " + Str(Val(Params(6))))
Print(" " + Str(Val(Params(7))))

PrintN("")

WriteSerialPortData(#SERIALPORT, *DataPacket, SizeOf(*DataPacket))
CloseSerialPort(#SERIALPORT)

; IDE Options = PureBasic 6.10 LTS (Linux - x64)
; ExecutableFormat = Console
; CursorPosition = 84
; FirstLine = 25
; Optimizer
; EnableXP
; DPIAware
; DisableDebugger
; CompileSourceDirectory