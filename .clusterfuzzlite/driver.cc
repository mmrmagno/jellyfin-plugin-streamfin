#include <libgen.h>
#include <limits.h>
#include <string>

#define LLVMFuzzerInitialize DotnetInitialize
#include "libfuzzer-dotnet.cc"
#undef LLVMFuzzerInitialize

extern "C" int LLVMFuzzerInitialize(int *argc, char ***argv)
{
    static char exe[PATH_MAX];
    ssize_t length = readlink("/proc/self/exe", exe, sizeof(exe) - 1);
    if (length <= 0) die_sys("readlink() failed");
    exe[length] = 0;
    static std::string path = std::string("--target_path=") + dirname(exe) + "/streamfin-fuzz/StreamFin.Fuzz";
    static std::string target = std::string("--target_arg=") + STREAMFIN_TARGET;
    static char *args[] = {(*argv)[0], path.data(), target.data(), nullptr};
    int count = 3;
    char **list = args;
    return DotnetInitialize(&count, &list);
}
