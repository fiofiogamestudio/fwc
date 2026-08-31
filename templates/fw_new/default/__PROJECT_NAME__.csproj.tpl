<Project Sdk="Godot.NET.Sdk/4.6.2">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <DefaultItemExcludes>$(DefaultItemExcludes);.godot/**;**/.godot/**</DefaultItemExcludes>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="csharp/**/*.cs" />
    <Compile Remove=".godot/**" />
    <Compile Remove=".godot\**" />
    <EmbeddedResource Remove=".godot/**" />
    <EmbeddedResource Remove=".godot\**" />
    <None Remove=".godot/**" />
    <None Remove=".godot\**" />
  </ItemGroup>
  <Import Project="csharp/_gen/_fw_game.props" Condition="Exists('csharp/_gen/_fw_game.props')" />
  <Target Name="RequireFwSync" BeforeTargets="PrepareForBuild" Condition="!Exists('csharp/_gen/_fw_game.props')">
    <Error Text="Fw kit references are missing. Run fw sync first." />
  </Target>
</Project>
