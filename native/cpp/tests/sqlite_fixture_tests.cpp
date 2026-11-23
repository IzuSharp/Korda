// SPDX-License-Identifier: MIT
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <iostream>
#include <string>

extern "C" std::uint32_t nw_b03_run_suite(const std::uint8_t* root, std::uint32_t length);

int main()
{
    const auto root = std::filesystem::temp_directory_path() /
                      ("korda-b03-cpp-" + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));
    const auto path = root.string();
    const auto status =
        nw_b03_run_suite(reinterpret_cast<const std::uint8_t*>(path.data()), static_cast<std::uint32_t>(path.size()));
    if (status == 0)
        std::filesystem::remove_all(root);
    else
        std::cerr << "B03 evidence retained at " << path << '\n';
    return static_cast<int>(status);
}
