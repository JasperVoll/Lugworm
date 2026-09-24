; 3D Potterbot Scara firmware Rev-3.1.1
; 2/8/2023
; Serial Number: SCV4-2373
; Motors: (Switches 3 and 4 everything)
; Z gear: I30
; Distal gear: 100:1
; Prox Gear: 100:1
; Full Z Rail Length: 57"
; Configuration file for Duet WiFi (firmware version 3.1.1 3DPotter Edition Rev10)
; executed by the firmware on start-up
;
;
; General preferences
G90                                                	; send absolute coordinates...
M550 P"Scara"                                      	; set printer name
;
; Network
M552 S2                                            	; broadcast wifi S2 OR S1 connect to network
M586 P0 S1                                          ; enable HTTP

; Drives and Movment
M569 P5 S1 R0 T4								   	; Drive 5 goes reverse  X
M569 P6 S1 R0 T4	 							   	; Drive 6 goes reverse  Y
M569 P7 S1 R0 T4						   		   	; Drive 7 goes fowards  Z
M569 P9 S1 R0 T4								   	; Drive 9 goes fowards  E
M584 X5 Y6 Z7 E9                                   	; set drive mapping
M350 X16 Y16 Z16 E16 I1                         	   	; configure microstepping with interpolation

                  			   																		
																				
													;		-------Drive Steps per Degree------
													;				(100:1)		(80:1)		(50:1)   
													;(40 drive X)	2653.10		736.36?		463.63
													;(25 drive Y)	2652.00     2945.44		1854.52

								
M92 X2653.10 Y2652.00							    ; Steps/degree X2653.10 Y2652.00
M92 Z803.59 E100000                   	   		   	; set steps per mm set to 100000 for actual mm
M566 X800 Y800 Z600 E60             	   	; set maximum instantaneous speed changes (mm/min)
M203 X3600 Y3600 Z1500 E75           	   	   	; set maximum speeds (mm/min)
M201 X800 Y800 Z500 E100                	   	; set accelerations (mm/s^2)
M906 X0 Y0 Z0 E0 I30                         	   	; set motor currents (mA) and motor idle factor in per cent
M84 S30                                            	; Set idle timeout

M669 K4 P549.06 D457.23 A-41.1:319.1 B-151.2:151.2 C0:0:0 T1 R235 		;V4 Arm-Clay Kinematics Config, Min Radius 235, T0 mirror the print bed


; Axis Limits
M208 X-300 Y-10000 Z0 S1                          ; set axis minima
M208 X10000 Y0 Z400 S0                          ; set axis maxima

; Endstops
M574 X1 S1 P"!xstop"                                ; configure active-high endstop for low end on X via pin xstop
M574 Y1 S1 P"!ystop"                               	; configure active-high endstop for low end on Y via pin ystop
M574 Z2 S1 P"!zstop"                               	; configure active-high endstop for high end on Z via pin zstop

; Tools
M563 S"clay" P0 D0		                        ; define tool 0
G10 P0 X0 Y0 Z0  									; set tool 0 axis offsets							  	
G10 P0 R0 S0                                       	; set initial tool 0 active and standby temperatures to 0C


; Miscellaneous
M911 S19.5 R21.4 P"M913 X0 Y0 G91 M83 G1 Z0.5 F1000" 	; set voltage thresholds and actions to run on power loss
T0                                                 	; select first tool
M302 P1     									   	; Allow cold extrudes
M83                                                	; ...but relative extruder moves
M558 P0 H5 I1 F120 T6000                           	; disable Z probe but set dive height, probe speed and travel speed
