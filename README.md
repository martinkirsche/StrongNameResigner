StrongNameResigner
==================

StrongNameResigner can sign or resign an existing .NET assembly and update all the references to it.

Why?
----

You can modify your existing strong name signed .NET assembly by decompile it using [Ildasm.exe], alter it as you like and compile it with [Ilasm.exe]. After that you can use the `-Vr` option of [Sn.exe] to register the assembly for verification skipping on your machine and everything will run fine. 

If you don't want to install and run [Sn.exe] on every machine your modified assembly must run, then you'll have to update all the references pointing to your assembly as well. This is particularly tricky because you will need to update..

  - the assembly references pointing to your assembly
  - the InternalsVisibleToAttribute reference strings
  - the custom attributes with a type parameter
  - the references within the XAML resources

...in your assembly and assemblies that depend on your assembly.

This is what StrongNameResigner tries to automate.

[Ildasm.exe]: https://msdn.microsoft.com/de-de/library/f7dy01k1%28v=vs.110%29.aspx
[Ilasm.exe]: https://msdn.microsoft.com/de-de/library/496e4ekx%28v=vs.110%29.aspx
[Sn.exe]: https://msdn.microsoft.com/de-de/library/k5b5tt23%28v=vs.110%29.aspx

Usage
-----

The following command will sign or resign an assembly named `Foo.Bar` and its dependencies stored in `c:\temp` using the key pair in `key.snk`:

    StrongNameResigner.exe -a "c:\temp" -k "key.snk" Foo.Bar

If no key pair is specified, the strong name signature will be removed.

The following command will only print a list of all assemblies that depend on `Foo.Bar`:

    StrongNameResigner.exe -d -a "c:\temp" Foo.Bar 

You can specify more than one assembly name for signing or resigning them and assemblies that depend on them at once.