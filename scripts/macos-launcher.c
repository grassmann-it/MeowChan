/* Native bundle launcher. Keep the .NET publish tree intact in Resources,
 * where its managed files, documentation and licenses are sealed as resources.
 * exec replaces this process; the real apphost's signature controls CoreCLR. */
#include <mach-o/dyld.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

int main(int argc, char **argv) {
    (void)argc;
    uint32_t size = 0;
    _NSGetExecutablePath(NULL, &size);
    char *buffer = malloc(size);
    if (!buffer || _NSGetExecutablePath(buffer, &size) != 0) {
        fprintf(stderr, "MeowChan: cannot locate bundle launcher\n");
        return 126;
    }
    char *launcher = realpath(buffer, NULL);
    free(buffer);
    if (!launcher) {
        perror("MeowChan: resolve bundle launcher");
        return 126;
    }
    char *slash = strrchr(launcher, '/');
    if (!slash) {
        free(launcher);
        return 126;
    }
    *slash = '\0';
    const char suffix[] = "/../Resources/application/MeowChan";
    char *candidate = malloc(strlen(launcher) + sizeof(suffix));
    if (!candidate) {
        free(launcher);
        return 126;
    }
    strcpy(candidate, launcher);
    strcat(candidate, suffix);
    free(launcher);
    char *apphost = realpath(candidate, NULL);
    free(candidate);
    if (!apphost) {
        perror("MeowChan: resolve application payload");
        return 126;
    }
    argv[0] = apphost;
    execv(apphost, argv);
    perror("MeowChan: start application payload");
    free(apphost);
    return 127;
}
