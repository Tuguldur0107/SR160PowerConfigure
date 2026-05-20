#include <stdio.h>
#include <stdint.h>
#include <dlfcn.h>

int main(void)
{
    void *lib = dlopen("./libUHFAPI.dylib", RTLD_NOW);
    if (!lib) {
        printf("dlopen failed: %s\n", dlerror());
        return 1;
    }

    int (*UsbOpen)(void) = dlsym(lib, "UsbOpen");
    void (*UsbClose)(void) = dlsym(lib, "UsbClose");
    int (*UHFGetPower)(uint8_t *) = dlsym(lib, "UHFGetPower");
    int (*UHFSetPower)(uint8_t, uint8_t) = dlsym(lib, "UHFSetPower");
    int (*UHFGetLastIOReturn)(void) = dlsym(lib, "UHFGetLastIOReturn");

    printf("Calling UsbOpen...\n");
    int ret = UsbOpen();
    printf("UsbOpen returned: %d\n", ret);

    if (ret == 0) {
        printf("Connected! Calling GetPower...\n");
        uint8_t power = 0;
        ret = UHFGetPower(&power);
        int ior = UHFGetLastIOReturn();
        printf("GetPower returned: %d, power=%d, IOReturn=0x%08X\n", ret, power, ior);

        printf("Calling SetPower(1, 20)...\n");
        ret = UHFSetPower(1, 20);
        ior = UHFGetLastIOReturn();
        printf("SetPower returned: %d, IOReturn=0x%08X\n", ret, ior);

        UsbClose();
        printf("Closed.\n");
    }

    dlclose(lib);
    return 0;
}
