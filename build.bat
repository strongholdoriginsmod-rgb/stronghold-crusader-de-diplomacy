@echo off
setlocal
if /i not "%~1"=="/devdiag" (
  echo Use build.py for a regular build, or build.bat /devdiag --devdiag-source ^<Shared DevDiag.cs^> with its normal build arguments.
  exit /b 1
)
if defined DIPLOMACY_BUILD_PYTHON (
  "%DIPLOMACY_BUILD_PYTHON%" -B "%~dp0build.py" %* --profile release --build
) else (
  py -3 -B "%~dp0build.py" %* --profile release --build
)
exit /b %errorlevel%
