#include "Bridge/NamedPipeServer.hpp"

#include <chrono>
#include <thread>

int main()
{
    PalControl::Bridge::NamedPipeServer bridge;
    bridge.Start("v1.0.0.100427-native-smoke", "0.1.0-native-smoke");
    std::this_thread::sleep_for(std::chrono::seconds(8));
    bridge.Stop();
    return bridge.IsRunning() ? 1 : 0;
}
