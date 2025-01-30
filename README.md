
# ES-Recorder
[engine-sim]() recording application with BeamNG.drive converting.
<br>
### This uses engine-sim 0.1.11a!
Newer engines are might not be compatible. I only added the convolution parameter to the engine declaration.

# Converting engines
The conversion process is pretty basic and mostly to make modding easier, not to make a mod for you.
You still need some modding knowledge to put the files in the correct directories and to mod the JBeam.

# Compiling
You need Visual Studio with C# and C++ packages, CMake and git to build this application.
## Clone both repositories (to different directories)
This application uses another repository for the engine-sim project. You need to compile both.

```sh

git clone https://github.com/DDev247/ESRecorder.git
git clone --recurse-submodules --branch esrecorder https://github.com/DDev247/engine-sim.git

```

## Build engine-sim
Open the engine-sim folder in Visual Studio and compile esrecord-lib.dll in Release.

## Copy library and build ESRecorder
Copy the resulting dll file into `ESRecorder/es/esrecord-lib.dll` and build the project in Release mode.
