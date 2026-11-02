import { createContext, useContext } from "react";

/** Set by `Field` so the control inside it points at its hint and error. */
export const FieldContext = createContext<{ "aria-describedby"?: string; "aria-invalid"?: true }>({});

export const useField = () => useContext(FieldContext);
