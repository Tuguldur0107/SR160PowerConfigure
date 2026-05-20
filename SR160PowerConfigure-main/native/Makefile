# Makefile for libUHFAPI — macOS native library (IOKit HID)
#
# No external dependencies required — uses macOS IOKit framework directly.
#
# Usage:
#   make          — build libUHFAPI.dylib
#   make install  — copy to runtimes/osx-x64/native/
#   make clean    — remove build artifacts

CC       = gcc
CFLAGS   = -Wall -Wextra -O2 -fPIC
LDFLAGS  = -shared -framework IOKit -framework CoreFoundation
TARGET   = libUHFAPI.dylib
SRC      = libUHFAPI.c

RUNTIME_DIR = ../runtimes/osx-x64/native

.PHONY: all install clean

all: $(TARGET)

$(TARGET): $(SRC)
	$(CC) $(CFLAGS) -o $@ $< $(LDFLAGS)
	@echo ""
	@echo "Built $(TARGET) successfully (IOKit HID)."

install: $(TARGET)
	@mkdir -p $(RUNTIME_DIR)
	cp $(TARGET) $(RUNTIME_DIR)/
	@echo "Installed to $(RUNTIME_DIR)/$(TARGET)"

clean:
	rm -f $(TARGET)
