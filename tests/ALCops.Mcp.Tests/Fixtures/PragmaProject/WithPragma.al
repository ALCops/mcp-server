#pragma warning disable
codeunit 50101 "With Pragma"
{
    procedure PublicProcedureWithoutDocs()
    begin
        Message('Hello');
    end;
}
#pragma warning restore
