#include "textflag.h"

TEXT ·spin32(SB),NOSPLIT,$0-0
	MOVQ $32, CX
loop:
	PAUSE
	DECQ CX
	JNZ loop
	RET
