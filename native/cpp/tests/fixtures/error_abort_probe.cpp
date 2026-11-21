#include <cstdlib>
#include <string_view>

// Explicit abort remains outside exception containment, including builds with
// unwinding disabled. It is exercised in an isolated child process.
int main(int argc, char** argv)
{
    if (argc == 2 && std::string_view(argv[1]) == "abort")
    {
        std::abort();
    }
    return 2;
}
