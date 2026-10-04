#pragma warning disable LC0020
page 50102 PageWithPragma
{
    ApplicationArea = All;

    layout
    {
        area(content)
        {
            field(MyField; MyField)
            {
                ApplicationArea = All;
            }
        }
    }

    var
        MyField: Text;
}
#pragma warning restore LC0020
