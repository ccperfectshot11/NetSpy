@echo off

pushd Extensions\Decompiler\NRefactory && git clean -xdf && popd || goto :error
pushd Libraries\NetSpy.TreeView && git clean -xdf && popd || goto :error
pushd Extensions\Decompiler\NetSpy.Decompiler && git clean -xdf && popd || goto :error
pushd NetSpy\NetSpy.Images && git clean -xdf && popd || goto :error
pushd Extensions\NetSpy.Debugger\netcorefiles && git clean -xdf && popd || goto :error
pushd NetSpy\Roslyn\Roslyn.ExpressionCompiler && git clean -xdf && popd || goto :error
pushd Extensions\NetSpy.Debugger\Mono.Debugger.Soft && git clean -xdf && popd || goto :error
git clean -xdf || goto :error

goto :EOF

:error
exit /b %errorlevel%
